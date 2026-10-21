using System.Diagnostics;
using Lumy.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace Lumy.App;

/// <summary>What and how to export, as chosen in the dialog.</summary>
/// <param name="Originals">Put the original files in the folder as hard links instead of writing edited JPEGs.</param>
/// <param name="Numbered">Prefix 01_, 02_… so the files sort in carousel order.</param>
public sealed record ExportOptions(string Folder, ExportSize Size, int Quality, bool Originals, bool Numbered, bool OpenFolder);

/// <summary>
/// Ctrl+E or "Экспорт": JPEGs for Instagram with the crop and sliders applied, 1080 px wide by default. Existing
/// files are never overwritten. An album can also go out as its original files, linked rather than copied.
/// </summary>
public sealed partial class MainWindow
{
    private bool _exporting;

    private async void Export_Click(object sender, RoutedEventArgs e) => await ExportAsync(null);

    /// <summary>Asks what to export (unless <paramref name="photos"/> says) and how, then does it.</summary>
    private async Task ExportAsync(List<PhotoItem>? photos, bool album = false)
    {
        if (_exporting)
        {
            ShowToast("Экспорт уже идёт");
            return;
        }

        // What to export, best guess first: the photos given, the selection, the album, the likes.
        var choices = new List<(string Label, List<PhotoItem> Photos, bool Numbered)>();
        if (photos is { Count: > 0 })
            choices.Add((album && _album is { } open ? $"Весь альбом «{open.Name}» ({photos.Count})"
                : photos.Count == 1 ? photos[0].FileName : $"Выбранные ({photos.Count})", photos, album));
        else
        {
            var selected = CurrentPhotos();
            if (selected.Count > 0)
                choices.Add((selected.Count == 1 ? selected[0].FileName : $"Выделенные ({selected.Count})", selected, false));
            if (_album is { } open && _all.Count > 0)
                choices.Add(($"Весь альбом «{open.Name}» ({_all.Count})", _all.ToList(), true));
            var liked = _all.Where(p => p.Tier >= Tier.Like).ToList();
            if (liked.Count > 0)
                choices.Add(($"Лайки и суперлайки ({liked.Count})", liked, false));
            var supers = _all.Where(p => p.Tier == Tier.SuperLike).ToList();
            if (supers.Count > 0 && supers.Count != liked.Count)
                choices.Add(($"Только суперлайки ({supers.Count})", supers, false));
        }

        if (choices.Count == 0)
        {
            ShowToast("Нечего экспортировать: выделите фото или поставьте лайки");
            return;
        }

        if (await AskExportAsync(choices) is not { } answer)
            return;

        await RunExportAsync(answer.Photos, answer.Options);
    }

    private async Task<(List<PhotoItem> Photos, ExportOptions Options)?> AskExportAsync(List<(string Label, List<PhotoItem> Photos, bool Numbered)> choices)
    {
        var what = new RadioButtons { Header = "Что" };
        foreach (var choice in choices)
            what.Items.Add(choice.Label);
        what.SelectedIndex = 0;

        var how = new RadioButtons { Header = "Как" };
        how.Items.Add("JPEG с кропом и цветом");
        how.Items.Add("Оригиналы как есть (без копирования, ссылками)");
        how.SelectedIndex = 0;

        var size = new ComboBox { Header = "Размер", MinWidth = 300 };
        size.Items.Add(new ComboBoxItem { Content = "Для Instagram: 1080 px по ширине", Tag = ExportSize.Instagram });
        size.Items.Add(new ComboBoxItem { Content = "2048 px по длинной стороне", Tag = ExportSize.LongEdge2048 });
        size.Items.Add(new ComboBoxItem { Content = "Полный размер", Tag = ExportSize.Full });
        size.SelectedIndex = (int)_settings.ExportSize;

        var quality = new Slider { Header = "Качество JPEG", Minimum = 70, Maximum = 100, StepFrequency = 1, Value = _settings.ExportQuality };
        var numbered = new CheckBox { Content = "Нумеровать файлы по порядку (01_, 02_…) для карусели", IsChecked = choices[0].Numbered };
        var open = new CheckBox { Content = "Открыть папку после экспорта", IsChecked = _settings.ExportOpenFolder };

        var folder = _settings.ExportFolder ?? DefaultExportFolder();
        var folderText = new TextBlock { Text = folder, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
        var pick = new Button { Content = "Другая папка…" };
        pick.Click += async (_, _) =>
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (await picker.PickSingleFolderAsync() is { } picked)
                folderText.Text = folder = picked.Path;
        };

        void Sync()
        {
            var jpeg = how.SelectedIndex == 0;
            size.IsEnabled = jpeg;
            quality.IsEnabled = jpeg;
            if (what.SelectedIndex >= 0)
                numbered.IsChecked = choices[what.SelectedIndex].Numbered;
        }

        what.SelectionChanged += (_, _) => Sync();
        how.SelectionChanged += (_, _) => Sync();
        Sync();

        var dialog = new ContentDialog
        {
            Title = "Экспорт",
            Content = new ScrollViewer
            {
                Content = new StackPanel
                {
                    Spacing = 14,
                    MinWidth = 420,
                    Children =
                    {
                        what, how, size, quality, numbered,
                        new StackPanel { Spacing = 6, Children = { new TextBlock { Text = "Папка" }, folderText, pick } },
                        open,
                    },
                },
            },
            PrimaryButtonText = "Экспортировать",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;

        var chosen = choices[Math.Max(0, what.SelectedIndex)];
        var exportSize = (size.SelectedItem as ComboBoxItem)?.Tag is ExportSize s ? s : ExportSize.Instagram;
        _settings.ExportFolder = folder;
        _settings.ExportSize = exportSize;
        _settings.ExportQuality = (int)quality.Value;
        _settings.ExportOpenFolder = open.IsChecked == true;

        // An album goes into a folder of its own name, so carousels do not mix.
        var target = chosen.Numbered && _album is { } album ? System.IO.Path.Combine(folder, SafeFolderName(album.Name)) : folder;
        return (chosen.Photos, new ExportOptions(target, exportSize, (int)quality.Value, how.SelectedIndex == 1, numbered.IsChecked == true, open.IsChecked == true));
    }

    /// <summary>The settings chosen last time, for exporting from the phone without a dialog on the computer.</summary>
    private ExportOptions SavedExportOptions(bool album)
    {
        var folder = _settings.ExportFolder ?? DefaultExportFolder();
        if (album && _album is { } open)
            folder = System.IO.Path.Combine(folder, SafeFolderName(open.Name));
        return new ExportOptions(folder, _settings.ExportSize, _settings.ExportQuality, Originals: false, Numbered: album, _settings.ExportOpenFolder);
    }

    private static string DefaultExportFolder() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Lumy Export");

    private static string SafeFolderName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? "Альбом" : clean;
    }

    /// <summary>Exports the photos one by one; returns how many files were written.</summary>
    private async Task<int> RunExportAsync(List<PhotoItem> photos, ExportOptions options)
    {
        if (photos.Count == 0)
            return 0;

        _exporting = true;
        var written = 0;
        var failed = new List<string>();
        var copies = 0;
        try
        {
            Directory.CreateDirectory(options.Folder);
            for (var i = 0; i < photos.Count; i++)
            {
                var photo = photos[i];
                ShowToast($"Экспорт: {i + 1} из {photos.Count}…");
                try
                {
                    var name = ExportPlan.FileName(photo.Path, options.Numbered ? i + 1 : null, photos.Count);
                    if (options.Originals)
                    {
                        // The original file and, for a RAW+JPEG pair, its RAW too, under the same running number.
                        foreach (var source in new[] { photo.Path, photo.RawPath }.OfType<string>().Distinct())
                        {
                            var target = ExportPlan.UniquePath(options.Folder, System.IO.Path.ChangeExtension(name, System.IO.Path.GetExtension(source)), File.Exists);
                            if (!await Task.Run(() => FileLinks.LinkOrCopy(source, target)))
                                copies++;
                        }
                    }
                    else
                    {
                        var target = ExportPlan.UniquePath(options.Folder, name, File.Exists);
                        if (!await ExportJpegAsync(photo, target, options))
                        {
                            failed.Add(photo.FileName);
                            continue;
                        }
                    }

                    written++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    failed.Add($"{photo.FileName} ({e.Message})");
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowToast($"Не удалось создать папку: {e.Message}");
            return written;
        }
        finally
        {
            _exporting = false;
        }

        var summary = written == 1 ? "Экспортировано 1 фото" : $"Экспортировано фото: {written}";
        if (options.Originals && copies > 0)
            summary += $" (скопировано {copies}: другой диск)";
        if (failed.Count > 0)
            summary += $". Не получилось: {string.Join(", ", failed.Take(3))}{(failed.Count > 3 ? "…" : "")}";
        ShowToast(summary);

        if (options.OpenFolder && written > 0)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{options.Folder}\"") { UseShellExecute = true });
            }
            catch (Exception)
            {
            }
        }

        return written;
    }

    /// <summary>Decodes at full resolution, crops, applies the sliders, scales and writes one JPEG.</summary>
    private static async Task<bool> ExportJpegAsync(PhotoItem photo, string target, ExportOptions options)
    {
        var edit = photo.Edit ?? PhotoEdit.None;
        return await Task.Run(async () =>
        {
            if (await PhotoDecoder.DecodeAsync(photo.Path, maxEdge: 0) is not { } decoded)
                return false;

            // Decoded may be smaller than the photo (a RAW's embedded preview), so the crop is taken in its pixels.
            var (x, y, width, height) = CropMath.ToPixels(edit.Crop, decoded.Width, decoded.Height);
            var pixels = edit.Crop.IsFull ? decoded.Pixels : PhotoDecoder.Crop(decoded, x, y, width, height);
            ImageAdjust.Apply(pixels, width, height, width * 4, edit);
            var (outWidth, outHeight) = ExportPlan.TargetSize(options.Size, width, height);
            await PhotoDecoder.SaveJpegAsync(pixels, width, height, outWidth, outHeight, options.Quality, target);
            return true;
        });
    }
}
