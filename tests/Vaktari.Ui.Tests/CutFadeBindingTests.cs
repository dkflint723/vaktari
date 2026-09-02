using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.XamlIl.Runtime;
using Avalonia.VisualTree;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// That a row can actually reach the shell to ask what has been cut.
///
/// **A binding that does not resolve leaves Opacity at 1.0**, which is exactly
/// what an uncut row looks like — so the feature would appear to do nothing and
/// nothing would be logged where anybody would see it. The listing templates had
/// no binding to the window before this, so the form was unproven in that
/// position: an item inside an ItemsControl has a different parent chain from a
/// context menu, which is where the pattern was already in use.
///
/// The structure is what is being tested, not the markup file: a window, a
/// listing bound to it, and an item template reaching back up. If that resolves
/// here it resolves in the real templates, which are the same shape.
/// </summary>
public sealed class CutFadeBindingTests : OwnedViewModels
{
    private const string Markup = """
        <Window xmlns="https://github.com/avaloniaui"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:vm="clr-namespace:Vaktari.Ui.ViewModels;assembly=Vaktari.Ui">
          <ItemsControl x:Name="Rows">
            <ItemsControl.ItemTemplate>
              <DataTemplate>
                <Border x:Name="Row" Height="20">
                  <Border.Opacity>
                    <MultiBinding Converter="{x:Static vm:FileConverters.CutFade}">
                      <Binding Path="."/>
                      <Binding Path="$parent[Window].((vm:ShellViewModel)DataContext).CutPaths"/>
                    </MultiBinding>
                  </Border.Opacity>
                </Border>
              </DataTemplate>
            </ItemsControl.ItemTemplate>
          </ItemsControl>
        </Window>
        """;

    [AvaloniaFact]
    public void A_row_reaches_the_shell_and_dims_when_its_path_is_cut()
    {
        CutMarks.Clear();

        var window = (Window)AvaloniaRuntimeXamlLoader.Load(Markup);
        var shell = Own(new ShellViewModel(new InertFileSystem()));

        window.DataContext = shell;

        var rows = window.FindControl<ItemsControl>("Rows")!;
        rows.ItemsSource = new[] { "/a/one.txt", "/a/two.txt" };

        window.Show();
        window.Measure(new Size(300, 200));
        window.Arrange(new Rect(0, 0, 300, 200));

        var borders = window.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "Row").ToList();

        Assert.Equal(2, borders.Count);
        Assert.All(borders, b => Assert.Equal(1.0, b.Opacity));

        // The moment something is cut, every visible row re-evaluates — which is
        // the whole reason the SET is bound rather than a per-row flag.
        shell.CutPaths = new HashSet<string> { "/a/one.txt" };

        window.Measure(new Size(300, 200));
        window.Arrange(new Rect(0, 0, 300, 200));

        Assert.True(borders[0].Opacity < 1.0, "the cut row should be dimmed");
        Assert.Equal(1.0, borders[1].Opacity);

        window.Close();
    }

    /// <summary>A file system that does nothing, so a shell can be built without
    /// touching a disk.</summary>
    private sealed class InertFileSystem : Vaktari.Core.FileSystem.IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<Vaktari.Core.FileSystem.FileEntry>> EnumerateAsync(
            string path, Vaktari.Core.FileSystem.ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<Vaktari.Core.FileSystem.FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<Vaktari.Core.FileSystem.FileEntry?>(null);

        public IDisposable Watch(string path, Action<Vaktari.Core.FileSystem.FileSystemChange> onChange)
            => new Nothing();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}
