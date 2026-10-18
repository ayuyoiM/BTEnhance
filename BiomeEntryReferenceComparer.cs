#nullable enable
using System.Collections.Generic;
using BTitles;

namespace BTitlesLocalizationPatch
{
    /*
    BiomeEntry 没重写 Equals，这里仍显式按引用比较，
    避免将来上游重写后，把两个「值相等」的条目当成同一个
    放在根命名空间而不是 Scan/：扫描侧的待补色集合与 Hook 侧的原色留底表
    用的是同一套语义，谁都不该依赖对方的内部类型
    */
    internal sealed class BiomeEntryReferenceComparer : IEqualityComparer<BiomeEntry>
    {
        public static readonly BiomeEntryReferenceComparer Instance = new();

        public bool Equals(BiomeEntry? x, BiomeEntry? y) => ReferenceEquals(x, y);

        public int GetHashCode(BiomeEntry obj) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
