using System.Collections.Generic;
using InspectionView.Core.Backends;
using InspectionView.Core.Errors;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;

namespace InspectionView.Core.Operators.Regions
{
    /// <summary>
    /// Region 을 연결 성분으로 분리한다 (Halcon connection). 기본 8-연결.
    /// 결과는 첫 픽셀의 행 우선 순서로 정렬된다.
    /// 스크립트: <c>Regions = Connection(Region)</c>
    /// </summary>
    [Operator("Connection", Category = "Regions", Description = "Splits a region into its connected components (4 or 8 connectivity).")]
    public sealed class Connection : OperatorBase
    {
        [Input(Description = "Input region.")]
        public Region? Region { get; set; }

        [Input(Description = "Neighborhood: 4 or 8.", Default = 8)]
        public int Connectivity { get; set; }

        [Output(Description = "Connected components.")]
        public ObjectArray<Region>? Regions { get; private set; }

        protected override void Validate()
        {
            Require(Region, nameof(Region));
            if (Connectivity != 4 && Connectivity != 8)
                throw VisionException.InvalidArgument(nameof(Connectivity), "must be 4 or 8");
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var runs = Region!.RunsArray;
            int n = runs.Length;
            var result = new ObjectArray<Region>();

            if (n == 0)
            {
                Regions = result;
                return;
            }

            // run 단위 union-find
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;

            int slack = Connectivity == 8 ? 1 : 0;
            int prevBegin = 0, prevEnd = 0;   // 이전 행 run 구간
            int i0 = 0;
            while (i0 < n)
            {
                int row = runs[i0].Row;
                int i1 = i0 + 1;
                while (i1 < n && runs[i1].Row == row) i1++;

                if (prevEnd > prevBegin && runs[prevBegin].Row == row - 1)
                {
                    int j = prevBegin;
                    for (int i = i0; i < i1; i++)
                    {
                        var a = runs[i];
                        while (j < prevEnd && runs[j].ColEnd + slack < a.ColBegin) j++;
                        for (int k = j; k < prevEnd && runs[k].ColBegin - slack <= a.ColEnd; k++)
                            Union(parent, i, k);
                    }
                }

                prevBegin = i0;
                prevEnd = i1;
                i0 = i1;
            }

            // 루트별로 run 을 모은다 (첫 등장 순서 유지 → 행 우선 정렬)
            var groups = new Dictionary<int, List<Run>>();
            var order = new List<int>();
            for (int i = 0; i < n; i++)
            {
                int root = Find(parent, i);
                if (!groups.TryGetValue(root, out var list))
                {
                    list = new List<Run>();
                    groups[root] = list;
                    order.Add(root);
                }
                list.Add(runs[i]);
            }

            foreach (var root in order)
                result.AddOwned(Types.Region.FromNormalizedRuns(groups[root].ToArray()));

            Regions = result;
        }

        private static int Find(int[] parent, int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a), rb = Find(parent, b);
            if (ra == rb) return;
            if (ra < rb) parent[rb] = ra;   // 작은 인덱스를 루트로 → 첫 등장 순서 보존
            else parent[ra] = rb;
        }
    }
}
