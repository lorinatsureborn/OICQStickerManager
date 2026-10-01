namespace OICQStickerManager.Models;

/// <summary>
/// 侧栏选项卡条目：Value 是选中态使用的稳定身份（标签名或 qq:&lt;uin&gt;），
/// Label 是显示文本（QQ 页改名只动 Label 不动 Value）。
/// </summary>
public class TabItemModel
{
    public TabItemModel(string value, string label, bool isQq = false)
    {
        Value = value;
        Label = label;
        IsQq = isQq;
    }

    public string Value { get; }

    public string Label { get; }

    /// <summary>QQ 绑定页：右键菜单走 重命名/解绑，普通标签页走 删除标签及其图片。</summary>
    public bool IsQq { get; }

    /// <summary>「删除标签及其图片」菜单项的显隐：QQ 页与「最近」默认视图不可删。</summary>
    public bool CanDeleteTags => !IsQq && Value != "最近";

    // ItemAutomationPeer 取名称的标准来源，读屏软件念标签文本而非类型名
    public override string ToString() => Label;
}
