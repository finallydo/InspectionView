using System;
using System.Collections.Generic;
using System.Linq;
using InspectionView.Core.Errors;
using InspectionView.Core.Memory;
using OpenCvSharp;
using ErrorCode = InspectionView.Core.Errors.ErrorCode;

namespace InspectionView.Core.Types
{
    /// <summary>
    /// RLE(Run-Length) 기반 영역. 불변(immutable) 객체이며 모든 연산은 새 Region 을 반환한다.
    /// <para>
    /// OpenCV / GPU 연산에서 마스크가 필요하면 <see cref="ToMask(RectI)"/> 로 변환하며, 결과는 캐시된다.
    /// Region 은 불변이므로 캐시는 해제 시까지 유효하다.
    /// </para>
    /// </summary>
    public sealed class Region : IconicObject
    {
        private readonly Run[] _runs;
        private readonly object _cacheLock = new object();

        private bool _bboxComputed;
        private RectI _bbox;
        private long _area = -1;

        private Mat? _maskCache;
        private RectI _maskCacheRect;

        public override string TypeName => "Region";

        /// <summary>정렬/병합된 run 목록 (row 오름차순, 같은 row 내 col 오름차순, 겹침 없음).</summary>
        public IReadOnlyList<Run> Runs => _runs;

        public int RunCount => _runs.Length;
        public bool IsEmpty => _runs.Length == 0;

        /// <summary>빈 영역.</summary>
        public static Region Empty => new Region(Array.Empty<Run>(), normalized: true);

        /// <summary>임의 run 집합으로 생성. 정렬/병합을 수행한다.</summary>
        public Region(IEnumerable<Run> runs)
            : this(Normalize(runs?.ToArray() ?? throw VisionException.NullArgument(nameof(runs))), normalized: true)
        {
        }

        private Region(Run[] runs, bool normalized)
        {
            _runs = normalized ? runs : Normalize(runs);
        }

        /// <summary>연산자 구현용: 이미 정렬/병합된 run 배열을 복사 없이 감싼다. 배열은 호출 후 수정하면 안 된다.</summary>
        internal static Region FromNormalizedRuns(Run[] runs) => new Region(runs, normalized: true);

        /// <summary>연산자 구현용: 내부 run 배열 (읽기 전용으로만 사용).</summary>
        internal Run[] RunsArray => _runs;

        /// <summary><paramref name="row"/> 이상인 첫 run 의 인덱스 (없으면 RunCount).</summary>
        public int LowerBoundRow(int row)
        {
            int lo = 0, hi = _runs.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (_runs[mid].Row < row) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        // ------------------------------------------------------------------
        // 생성 팩토리
        // ------------------------------------------------------------------

        /// <summary>사각형 영역 (배타 경계).</summary>
        public static Region FromRectangle(RectI rect)
        {
            if (rect.IsEmpty)
                return Empty;

            var runs = new Run[rect.Height];
            for (int i = 0; i < rect.Height; i++)
                runs[i] = new Run(rect.Y + i, rect.X, rect.Right - 1);
            return new Region(runs, normalized: true);
        }

        /// <summary>Halcon gen_rectangle1 과 동일한 포함 좌표 사각형.</summary>
        public static Region FromRectangle1(int row1, int col1, int row2, int col2)
            => FromRectangle(RectI.FromInclusive(col1, row1, col2, row2));

        /// <summary>8bit 1채널 마스크(0 이 아닌 픽셀)에서 생성.</summary>
        public static Region FromMask(Mat mask, int offsetX = 0, int offsetY = 0)
        {
            if (mask == null)
                throw VisionException.NullArgument(nameof(mask));
            if (mask.Empty())
                return Empty;
            if (mask.Type() != MatType.CV_8UC1)
                throw new VisionException(ErrorCode.TypeMismatch, "Mask must be CV_8UC1.");

            return FromMask(mask.Data, mask.Cols, mask.Rows, mask.Step(), offsetX, offsetY);
        }

        /// <summary>8bit 1채널 메모리 블록에서 생성.</summary>
        public static unsafe Region FromMask(IntPtr data, int width, int height, long stride, int offsetX = 0, int offsetY = 0)
        {
            if (data == IntPtr.Zero)
                throw VisionException.NullArgument(nameof(data));

            var runs = new List<Run>();
            byte* basePtr = (byte*)data;
            for (int y = 0; y < height; y++)
            {
                byte* row = basePtr + stride * y;
                int x = 0;
                while (x < width)
                {
                    while (x < width && row[x] == 0) x++;
                    if (x >= width) break;
                    int start = x;
                    while (x < width && row[x] != 0) x++;
                    runs.Add(new Run(y + offsetY, start + offsetX, x - 1 + offsetX));
                }
            }
            return new Region(runs.ToArray(), normalized: true);
        }

        // ------------------------------------------------------------------
        // 특징값
        // ------------------------------------------------------------------

        /// <summary>픽셀 수.</summary>
        public long Area
        {
            get
            {
                if (_area < 0)
                {
                    long a = 0;
                    foreach (var r in _runs) a += r.Length;
                    _area = a;
                }
                return _area;
            }
        }

        /// <summary>바운딩 박스 (배타 경계). 빈 영역이면 Empty.</summary>
        public RectI BoundingBox
        {
            get
            {
                if (!_bboxComputed)
                {
                    if (_runs.Length == 0)
                    {
                        _bbox = RectI.Empty;
                    }
                    else
                    {
                        int minX = int.MaxValue, maxX = int.MinValue;
                        foreach (var r in _runs)
                        {
                            if (r.ColBegin < minX) minX = r.ColBegin;
                            if (r.ColEnd > maxX) maxX = r.ColEnd;
                        }
                        _bbox = RectI.FromInclusive(minX, _runs[0].Row, maxX, _runs[_runs.Length - 1].Row);
                    }
                    _bboxComputed = true;
                }
                return _bbox;
            }
        }

        /// <summary>픽셀 (x, y) 가 영역에 포함되는지. row 는 이진 탐색.</summary>
        public bool Contains(int x, int y)
        {
            int idx = FirstRunOfRow(y);
            if (idx < 0)
                return false;
            for (int i = idx; i < _runs.Length && _runs[i].Row == y; i++)
            {
                if (_runs[i].ColBegin > x) return false;
                if (_runs[i].ColEnd >= x) return true;
            }
            return false;
        }

        private int FirstRunOfRow(int row)
        {
            int lo = 0, hi = _runs.Length - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int r = _runs[mid].Row;
                if (r < row) lo = mid + 1;
                else
                {
                    if (r == row) found = mid;
                    hi = mid - 1;
                }
            }
            return found;
        }

        // ------------------------------------------------------------------
        // 마스크 변환 (캐시)
        // ------------------------------------------------------------------

        /// <summary>
        /// <paramref name="bounds"/> 크기의 CV_8UC1 마스크 (영역 내부 = 255).
        /// 반환된 Mat 은 Region 이 소유하는 캐시이므로 수정하거나 Dispose 하지 말 것.
        /// </summary>
        public Mat ToMask(RectI bounds)
        {
            ThrowIfDisposed();
            if (bounds.IsEmpty)
                throw VisionException.InvalidArgument(nameof(bounds), "must not be empty");

            lock (_cacheLock)
            {
                if (_maskCache != null && _maskCacheRect == bounds)
                    return _maskCache;

                var mask = new Mat(bounds.Height, bounds.Width, MatType.CV_8UC1, Scalar.All(0));
                Rasterize(mask, bounds);

                _maskCache?.Dispose();
                _maskCache = mask;
                _maskCacheRect = bounds;
                return mask;
            }
        }

        /// <summary>(0,0) 기준 width x height 마스크.</summary>
        public Mat ToMask(int width, int height) => ToMask(new RectI(0, 0, width, height));

        /// <summary>바운딩 박스 크기의 마스크.</summary>
        public Mat ToMask() => ToMask(BoundingBox);

        private unsafe void Rasterize(Mat mask, RectI bounds)
        {
            byte* basePtr = (byte*)mask.Data;
            long stride = mask.Step();
            foreach (var r in _runs)
            {
                int y = r.Row - bounds.Y;
                if (y < 0 || y >= bounds.Height) continue;

                int x0 = Math.Max(r.ColBegin, bounds.X) - bounds.X;
                int x1 = Math.Min(r.ColEnd, bounds.Right - 1) - bounds.X;
                if (x1 < x0) continue;

                new Span<byte>(basePtr + stride * y + x0, x1 - x0 + 1).Fill(255);
            }
        }

        // ------------------------------------------------------------------
        // 집합 / 기하 연산
        // ------------------------------------------------------------------

        public Region Translate(int dx, int dy)
        {
            var runs = new Run[_runs.Length];
            for (int i = 0; i < runs.Length; i++)
                runs[i] = new Run(_runs[i].Row + dy, _runs[i].ColBegin + dx, _runs[i].ColEnd + dx);
            return new Region(runs, normalized: true);
        }

        /// <summary>사각형 밖을 잘라낸다.</summary>
        public Region Clip(RectI bounds)
        {
            if (bounds.IsEmpty)
                return Empty;

            var runs = new List<Run>(_runs.Length);
            foreach (var r in _runs)
            {
                if (r.Row < bounds.Y || r.Row >= bounds.Bottom) continue;
                int b = Math.Max(r.ColBegin, bounds.X);
                int e = Math.Min(r.ColEnd, bounds.Right - 1);
                if (b <= e) runs.Add(new Run(r.Row, b, e));
            }
            return new Region(runs.ToArray(), normalized: true);
        }

        public Region Union(Region other)
        {
            if (other == null) throw VisionException.NullArgument(nameof(other));
            var all = new Run[_runs.Length + other._runs.Length];
            Array.Copy(_runs, 0, all, 0, _runs.Length);
            Array.Copy(other._runs, 0, all, _runs.Length, other._runs.Length);
            return new Region(all, normalized: false);
        }

        public Region Intersection(Region other)
        {
            if (other == null) throw VisionException.NullArgument(nameof(other));
            var a = _runs;
            var b = other._runs;
            var result = new List<Run>();

            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                int ra = a[i].Row, rb = b[j].Row;
                if (ra < rb) { i = RowEnd(a, i); continue; }
                if (rb < ra) { j = RowEnd(b, j); continue; }

                int ie = RowEnd(a, i), je = RowEnd(b, j);
                int p = i, q = j;
                while (p < ie && q < je)
                {
                    int lo = Math.Max(a[p].ColBegin, b[q].ColBegin);
                    int hi = Math.Min(a[p].ColEnd, b[q].ColEnd);
                    if (lo <= hi)
                        result.Add(new Run(ra, lo, hi));

                    if (a[p].ColEnd < b[q].ColEnd) p++;
                    else q++;
                }
                i = ie;
                j = je;
            }
            return new Region(result.ToArray(), normalized: true);
        }

        public Region Difference(Region other)
        {
            if (other == null) throw VisionException.NullArgument(nameof(other));
            var a = _runs;
            var b = other._runs;
            var result = new List<Run>(a.Length);

            int i = 0, j = 0;
            while (i < a.Length)
            {
                int ra = a[i].Row;
                int ie = RowEnd(a, i);

                while (j < b.Length && b[j].Row < ra)
                    j = RowEnd(b, j);

                if (j >= b.Length || b[j].Row != ra)
                {
                    for (int p = i; p < ie; p++) result.Add(a[p]);
                    i = ie;
                    continue;
                }

                int je = RowEnd(b, j);
                for (int p = i; p < ie; p++)
                {
                    int cur = a[p].ColBegin;
                    int end = a[p].ColEnd;
                    for (int q = j; q < je && cur <= end; q++)
                    {
                        var bq = b[q];
                        if (bq.ColEnd < cur) continue;
                        if (bq.ColBegin > end) break;
                        if (bq.ColBegin > cur)
                            result.Add(new Run(ra, cur, bq.ColBegin - 1));
                        cur = Math.Max(cur, bq.ColEnd + 1);
                    }
                    if (cur <= end)
                        result.Add(new Run(ra, cur, end));
                }
                i = ie;
                j = je;
            }
            return new Region(result.ToArray(), normalized: true);
        }

        /// <summary>같은 row 에 속한 run 구간의 끝 인덱스(배타).</summary>
        private static int RowEnd(Run[] runs, int start)
        {
            int row = runs[start].Row;
            int e = start + 1;
            while (e < runs.Length && runs[e].Row == row) e++;
            return e;
        }

        /// <summary>정렬 후 겹치거나 인접한 run 을 병합한다.</summary>
        private static Run[] Normalize(Run[] runs)
        {
            if (runs.Length == 0)
                return runs;

            var sorted = runs.Where(r => r.ColEnd >= r.ColBegin).ToArray();
            Array.Sort(sorted);

            var merged = new List<Run>(sorted.Length);
            foreach (var r in sorted)
            {
                if (merged.Count > 0)
                {
                    var last = merged[merged.Count - 1];
                    if (last.Row == r.Row && r.ColBegin <= last.ColEnd + 1)
                    {
                        merged[merged.Count - 1] = new Run(last.Row, last.ColBegin, Math.Max(last.ColEnd, r.ColEnd));
                        continue;
                    }
                }
                merged.Add(r);
            }
            return merged.ToArray();
        }

        protected override void ReleaseResources(bool disposing)
        {
            if (!disposing)
                return;

            lock (_cacheLock)
            {
                _maskCache?.Dispose();
                _maskCache = null;
            }
        }

        public override string ToString()
            => IsEmpty ? "Region (empty)" : $"Region area={Area} bbox={BoundingBox} runs={RunCount}";
    }
}
