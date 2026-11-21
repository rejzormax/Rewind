using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Rewind.Services;

namespace Rewind;

public sealed record ClipRow(IReadOnlyList<SavedClip> Clips, int Columns);

public sealed class VideoPreviewFrame : Decorator
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 320;
        var size = new Size(Math.Max(0, width), Math.Max(0, width * 9 / 16));
        Child?.Measure(size);
        return size;
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(new Point(), finalSize));
        Clip = new RectangleGeometry(new Rect(new Point(), finalSize), 7, 7);
        return finalSize;
    }
}
