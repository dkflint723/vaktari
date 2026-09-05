using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Vaktari.Ui.Thumbnails;

/// <summary>
/// Attached property that loads a thumbnail into an Image asynchronously.
///
/// Attached rather than a row view model wrapper because the list virtualizes:
/// only visible rows have a realized Image, so binding the path here makes
/// loading viewport-driven for free, with no change to what the collection
/// holds.
/// </summary>
public static class ThumbnailImage
{
    public static readonly AttachedProperty<string?> PathProperty =
        AvaloniaProperty.RegisterAttached<Image, string?>("Path", typeof(ThumbnailImage));

    public static readonly AttachedProperty<int> SizeProperty =
        AvaloniaProperty.RegisterAttached<Image, int>("Size", typeof(ThumbnailImage), 32);

    private static readonly AttachedProperty<CancellationTokenSource?> TokenProperty =
        AvaloniaProperty.RegisterAttached<Image, CancellationTokenSource?>("Token", typeof(ThumbnailImage));

    static ThumbnailImage()
    {
        PathProperty.Changed.AddClassHandler<Image>((image, e) =>
            OnPathChanged(image, e.NewValue as string));
    }

    public static void SetPath(Image image, string? value) => image.SetValue(PathProperty, value);
    public static string? GetPath(Image image) => image.GetValue(PathProperty);

    public static void SetSize(Image image, int value) => image.SetValue(SizeProperty, value);
    public static int GetSize(Image image) => image.GetValue(SizeProperty);

    /// <summary>
    /// Shows or hides the thumbnail AND the icon it stands in for.
    ///
    /// **A tile shows one or the other, never both.** Both live in the same
    /// Panel with the thumbnail on top, and `Stretch="Uniform"` fits a wide
    /// picture to the box's width — so a 16:9 wallpaper covers only a band
    /// across the middle and the generic mime icon shows above and below it.
    /// That read as a second icon hiding behind the picture.
    ///
    /// Done here rather than by binding the icon's `IsVisible` to this one's:
    /// element-name bindings appear NOWHERE in this codebase, and an idiom used
    /// nowhere else is evidence rather than style — the last one invented here
    /// (`IsVisible` bound to an `int`) failed at runtime, not at compile time.
    ///
    /// The sibling is identified by carrying `RowIcon.Entry`, not by being the
    /// only other Image, so adding a third layer to a tile cannot silently
    /// change what this hides.
    /// </summary>
    private static void SetShowing(Image image, bool showing)
    {
        image.IsVisible = showing;

        if (image.GetVisualParent() is not Panel panel) return;

        foreach (var child in panel.Children)
        {
            if (ReferenceEquals(child, image)) continue;

            if (child is Image icon && icon.IsSet(RowIcon.EntryProperty))
                icon.IsVisible = !showing;
        }
    }

    private static async void OnPathChanged(Image image, string? path)
    {
        // async void: nothing may escape, or a scroll turns into a crash.
        try
        {
            // Containers are recycled as you scroll, so the previous request is
            // abandoned — otherwise a fast scroll leaves the wrong picture on a
            // row.
            if (image.GetValue(TokenProperty) is { } previous)
            {
                previous.Cancel();
                previous.Dispose();
            }

            // Visibility is cleared WITH the source, not only on the failure
            // path below. A recycled container otherwise keeps `IsVisible=true`
            // from the previous file while holding no bitmap, and the icon
            // underneath stays hidden behind nothing at all.
            image.Source = null;
            SetShowing(image, false);

            if (string.IsNullOrEmpty(path) || !ThumbnailLoader.CanThumbnail(path))
            {
                image.SetValue(TokenProperty, null);
                return;
            }

            var cts = new CancellationTokenSource();
            image.SetValue(TokenProperty, cts);

            // Captured before awaiting. A later call on this same Image disposes
            // the source while we are still suspended, and reading .Token on a
            // disposed source throws — whereas the token struct itself stays
            // safe to query.
            var token = cts.Token;

            // In DEVICE pixels, the same as the icon under it — fixing one and
            // not the other would leave a crisp icon behind a soft picture in
            // the same cell. See DeviceSize.
            var bitmap = await ThumbnailLoader
                .LoadAsync(path, DeviceSize.For(image, image.GetValue(SizeProperty)), token)
                .ConfigureAwait(true);

            if (token.IsCancellationRequested) return;

            // The container may have been recycled onto a different file while
            // we were decoding; only paint if it still wants this one.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (GetPath(image) != path) return;

                image.Source = bitmap;
                SetShowing(image, bitmap is not null);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vaktari] thumbnail failed: {ex.Message}");
        }
    }
}
