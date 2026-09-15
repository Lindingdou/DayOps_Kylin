using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views;

public partial class MainWindow
{
    /// <summary>双击标注进入右侧特性编辑，并吞掉原来的“双击空白=范围缩放”动作。</summary>
    private bool TryBeginDimensionEditAt(Avalonia.Point p)
    {
        DimensionEntity? dimension;
        if (Viewport.Is2DView)
        {
            var world = Viewport.ScreenToWorld(p.X, p.Y);
            if (world == null) return false;
            dimension = PickWorld2D(world.Value.x, world.Value.y, SnapTolWorld(p)) as DimensionEntity;
        }
        else
        {
            dimension = SelectionBox.PickScreen(
                _scene.Entities, p.X, p.Y, _snapTolPx,
                Viewport.WorldToScreenDepthProjector(), _layers.IsSelectable) as DimensionEntity;
        }
        if (dimension == null) return false;
        if (TextEditing) EndTextEdit(commit: true);
        // 一旦命中标注，这次双击就由标注编辑接管；先结束可能继续消费鼠标的旧交互。
        ResetForDimensionInteraction();
        if (IsLayerLocked(dimension))
        {
            StatusMsg.Text = $"编辑标注：图层「{dimension.LayerName}」已锁定，不可编辑";
            return true;
        }

        _selected.Clear();
        _selected.Add(dimension);
        HighlightSelection();
        if (AiChatToggle != null) AiChatToggle.IsChecked = false;
        if (_dockFactory != null && _propsTool != null) _dockFactory.SetActiveDockable(_propsTool);
        RefreshScenePreview();
        StatusMsg.Text = "标注编辑：已进入编辑模式，可拖动蓝色节点调整高度，或在右侧特性面板修改";
        SyncPrompt();

        // 默认聚焦文字内容，用户双击后可直接输入；没有该行时聚焦第一个可编辑项。
        Dispatcher.UIThread.Post(() =>
        {
            var editors = PropertyPanel.GetVisualDescendants().OfType<TextBox>().ToList();
            var editor = editors.FirstOrDefault(x => Equals(x.Tag, "文字内容")) ?? editors.FirstOrDefault();
            if (editor == null) return;
            editor.Focus();
            editor.SelectAll();
        });
        return true;
    }
}
