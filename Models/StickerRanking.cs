namespace OICQStickerManager.Models;

/// <summary>
/// 表情排序热度分（frecency，思路同 Firefox 地址栏）：
///
///     RankScore = 频次因子 × 新近度权重
///
/// 频次因子 = 1 + log2(1 + UseCount)：0 次得 1，此后每翻一倍 +1（1/3/7/15/31 次 → 2/3/4/5/6），
/// 对数压平"刷次数"，高频表情不会无限碾压其他维度。
/// 新近度权重分桶取整（见 RecencyBuckets）：刚用过 100，一周内 35，一个月以上 10，
/// 「最近活跃」在乘法里天然占大头——同频次下刚用过的分数是冷却一个月的 10 倍。
///
/// 定性目标：最近添加/使用的排前面，最近使用的权重略高于最常使用；
/// 两因子相乘（而非相加）让"高频但冷却"与"低频但刚用"互相牵制，谁也不会一边倒。
/// LastUsedTime 语义为"最近一次活跃"：从未使用的表情以入库时间兜底，
/// 因此新表情高位起步、随时间自然下沉。
/// 排序权重如需调整只改这里，三处消费方（图库视图/左侧标签/快捷面板）共用同一把尺子。
/// </summary>
public static class StickerRanking
{
    /// <summary>新近度分桶：(不超过的时长, 权重)，从新到旧首中即停；边界内一档同权，避免分数抖动。</summary>
    private static readonly (TimeSpan MaxAge, double Weight)[] RecencyBuckets =
    {
        (TimeSpan.FromHours(1), 100),   // 刚刚用过
        (TimeSpan.FromHours(6), 85),    // 今天早些时候
        (TimeSpan.FromHours(24), 70),   // 24 小时内
        (TimeSpan.FromDays(3), 50),     // 近三天
        (TimeSpan.FromDays(7), 35),     // 本周
        (TimeSpan.FromDays(30), 20),    // 本月
        (TimeSpan.MaxValue, 10),        // 更久 / 从未活跃（冷却兜底档）
    };

    /// <summary>频次因子：1 + log2(1 + 次数)。</summary>
    public static double FrequencyFactor(int useCount)
        => 1.0 + Math.Log2(1 + Math.Max(0, useCount));

    /// <summary>新近度权重：距上次活跃的时长落桶取权重。</summary>
    public static double RecencyWeight(DateTime lastActive, DateTime now)
    {
        var age = now - lastActive;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero; // 时钟回拨等异常按"刚活跃"处理
        foreach (var (maxAge, weight) in RecencyBuckets)
            if (age <= maxAge) return weight;
        return RecencyBuckets[^1].Weight;
    }

    /// <summary>热度总分，图库/标签/快捷面板共用的唯一排序键。</summary>
    /// now 缺省时按分钟取底：Score 被 ListCollectionView.CustomSort 在一次排序里对每对元素
    /// 反复调用，直接读时钟的话，排序期间恰好有元素跨过分桶阶跃边界会让比较器前后不自洽
    ///（顺序抖动，ListCollectionView 可能直接抛"无法比较两个元素"）。分钟粒度对排序是
    /// 恒定时间源，对新近度分桶（最细 1 小时）无感。</summary>
    public static double Score(int useCount, DateTime lastActive, DateTime? now = null)
    {
        var t = now ?? DateTime.Now;
        if (now == null)
        {
            t = t.AddTicks(-(t.Ticks % TimeSpan.TicksPerMinute));
        }
        return FrequencyFactor(useCount) * RecencyWeight(lastActive, t);
    }
}
