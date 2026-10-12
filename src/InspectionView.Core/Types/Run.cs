using System;

namespace InspectionView.Core.Types
{
    /// <summary>
    /// Region 의 RLE 한 구간. <see cref="ColEnd"/> 는 포함(inclusive) 이다. (Halcon 의 runs 와 동일)
    /// </summary>
    public readonly struct Run : IEquatable<Run>, IComparable<Run>
    {
        public int Row { get; }
        public int ColBegin { get; }
        public int ColEnd { get; }

        public int Length => ColEnd - ColBegin + 1;

        public Run(int row, int colBegin, int colEnd)
        {
            Row = row;
            ColBegin = colBegin;
            ColEnd = colEnd;
        }

        public bool Contains(int x) => x >= ColBegin && x <= ColEnd;

        public bool Overlaps(Run other)
            => Row == other.Row && ColBegin <= other.ColEnd && other.ColBegin <= ColEnd;

        public int CompareTo(Run other)
        {
            int c = Row.CompareTo(other.Row);
            return c != 0 ? c : ColBegin.CompareTo(other.ColBegin);
        }

        public bool Equals(Run other)
            => Row == other.Row && ColBegin == other.ColBegin && ColEnd == other.ColEnd;

        public override bool Equals(object? obj) => obj is Run r && Equals(r);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = Row;
                h = h * 397 ^ ColBegin;
                h = h * 397 ^ ColEnd;
                return h;
            }
        }

        public override string ToString() => $"row {Row}: [{ColBegin}..{ColEnd}]";
    }
}
