using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

using Microsoft.Win32;

using Windows.Win32;
using Windows.Win32.UI.Shell;
using Windows.Win32.Foundation;

using CommunityToolkit.Mvvm.Input;

using Bloxstrap.Models.SettingTasks;
using Bloxstrap.AppData;

namespace Bloxstrap.UI.ViewModels.Settings
{
    public class ModsViewModel : NotifyPropertyChangedViewModel
    {
        private void OpenModsFolder() => Process.Start("explorer.exe", Paths.Modifications);

        private readonly Dictionary<string, byte[]> FontHeaders = new()
        {
            { "ttf", new byte[4] { 0x00, 0x01, 0x00, 0x00 } },
            { "otf", new byte[4] { 0x4F, 0x54, 0x54, 0x4F } },
            { "ttc", new byte[4] { 0x74, 0x74, 0x63, 0x66 } } 
        };

        private void ManageCustomFont()
        {
            if (!String.IsNullOrEmpty(TextFontTask.NewState))
            {
                TextFontTask.NewState = "";
            }
            else
            {
                var dialog = new OpenFileDialog
                {
                    Filter = $"{Strings.Menu_FontFiles}|*.ttf;*.otf;*.ttc"
                };

                if (dialog.ShowDialog() != true)
                    return;

                string type = dialog.FileName.Substring(dialog.FileName.Length-3, 3).ToLowerInvariant();

                if (!FontHeaders.ContainsKey(type) 
                    || !FontHeaders.Any(x => File.ReadAllBytes(dialog.FileName).Take(4).SequenceEqual(x.Value)))
                {
                    Frontend.ShowMessageBox(Strings.Menu_Mods_Misc_CustomFont_Invalid, MessageBoxImage.Error);
                    return;
                }

                TextFontTask.NewState = dialog.FileName;
            }

            OnPropertyChanged(nameof(ChooseCustomFontVisibility));
            OnPropertyChanged(nameof(DeleteCustomFontVisibility));
        }

        public ICommand OpenModsFolderCommand => new RelayCommand(OpenModsFolder);

        // Custom cursors: a Windows cursor package is reduced to the one pointer
        // image Roblox actually draws, then written to the mods folder.
        public ObservableCollection<CursorSetItem> CursorSets { get; } = new();

        private bool _refreshingCursorSets;
        private CursorSetItem? _selectedCursorSet;

        public sealed class CursorSetItem
        {
            public Utility.CustomCursors.CursorSet Set { get; init; } = null!;
            public string Name => Set.Name;
            public BitmapSource? Image { get; init; }
        }

        public CursorSetItem? SelectedCursorSet
        {
            get => _selectedCursorSet;
            set
            {
                if (ReferenceEquals(_selectedCursorSet, value)) return;
                _selectedCursorSet = value;
                OnPropertyChanged(nameof(SelectedCursorSet));
                OnPropertyChanged(nameof(SelectedCursorSetImage));
                if (_refreshingCursorSets) return;
                try
                {
                    Utility.CustomCursors.Apply(value?.Set);
                }
                catch (Exception ex)
                {
                    Frontend.ShowMessageBox("Could not apply that cursor set.\n\n" + ex.Message, MessageBoxImage.Error);
                    ReloadCursorSets();
                }
            }
        }

        public BitmapSource? SelectedCursorSetImage => _selectedCursorSet?.Image;

        public ICommand OpenCursorLibraryCommand => new RelayCommand(OpenCursorLibrary);

        public ICommand ImportCursorCommand => new RelayCommand(ImportCursor);

        public ICommand RemoveCursorCommand => new RelayCommand(RemoveCursor);

        private static void OpenCursorLibrary()
        {
            Process.Start(new ProcessStartInfo(Utility.CustomCursors.LibraryUrl) { UseShellExecute = true });
        }

        private static BitmapSource? LoadImage(string path)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception) { return null; }
        }

        private void ReloadCursorSets()
        {
            _refreshingCursorSets = true;
            try
            {
                CursorSets.Clear();
                foreach (var set in Utility.CustomCursors.List())
                    CursorSets.Add(new CursorSetItem { Set = set, Image = LoadImage(set.ImagePath) });

                var active = Utility.CustomCursors.Active();
                _selectedCursorSet = active is null ? null : CursorSets.FirstOrDefault(x => x.Set.Id == active.Id);
                OnPropertyChanged(nameof(SelectedCursorSet));
                OnPropertyChanged(nameof(SelectedCursorSetImage));
            }
            finally { _refreshingCursorSets = false; }
        }

        private void ImportCursor()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Import a cursor package",
                Filter = "Cursor packages (*.zip;*.cur;*.ani;*.inf)|*.zip;*.cur;*.ani;*.inf|All files (*.*)|*.*",
                CheckFileExists = true
            };

            // Start on the newest cursor download so "Install zip" from a cursor
            // site can be imported with one click.
            string? recent = Utility.CustomCursors.FindRecentDownload();
            if (recent is not null) dialog.FileName = Path.GetFileName(recent);
            if (recent is not null) dialog.InitialDirectory = Path.GetDirectoryName(recent);

            if (dialog.ShowDialog() != true) return;

            try
            {
                var imported = Utility.CustomCursors.Import(dialog.FileName);
                ReloadCursorSets();
                SelectedCursorSet = CursorSets.FirstOrDefault(x => x.Set.Id == imported.Id) ?? SelectedCursorSet;
            }
            catch (Exception ex)
            {
                Frontend.ShowMessageBox("That file does not contain a usable Windows cursor.\n\n" + ex.Message, MessageBoxImage.Warning);
            }
        }

        private void RemoveCursor()
        {
            if (_selectedCursorSet is null)
            {
                Frontend.ShowMessageBox("Choose a custom cursor set first.", MessageBoxImage.Information);
                return;
            }

            try
            {
                Utility.CustomCursors.Remove(_selectedCursorSet.Set);
                ReloadCursorSets();
            }
            catch (Exception ex)
            {
                Frontend.ShowMessageBox("Could not remove that cursor set.\n\n" + ex.Message, MessageBoxImage.Warning);
            }
        }

        public Visibility ChooseCustomFontVisibility => !String.IsNullOrEmpty(TextFontTask.NewState) ? Visibility.Collapsed : Visibility.Visible;

        public Visibility DeleteCustomFontVisibility => !String.IsNullOrEmpty(TextFontTask.NewState) ? Visibility.Visible : Visibility.Collapsed;

        public ICommand ManageCustomFontCommand => new RelayCommand(ManageCustomFont);

        public ICommand OpenCompatSettingsCommand => new RelayCommand(OpenCompatSettings);

        public ModPresetTask OldAvatarBackgroundTask { get; } = new("OldAvatarBackground", @"ExtraContent\places\Mobile.rbxl", "OldAvatarBackground.rbxl");

        public ModPresetTask OldCharacterSoundsTask { get; } = new("OldCharacterSounds", new()
        {
            { @"content\sounds\action_footsteps_plastic.mp3", "Sounds.OldWalk.mp3"  },
            { @"content\sounds\action_jump.mp3",              "Sounds.OldJump.mp3"  },
            { @"content\sounds\action_get_up.mp3",            "Sounds.OldGetUp.mp3" },
            { @"content\sounds\action_falling.mp3",           "Sounds.Empty.mp3"    },
            { @"content\sounds\action_jump_land.mp3",         "Sounds.Empty.mp3"    },
            { @"content\sounds\action_swim.mp3",              "Sounds.Empty.mp3"    },
            { @"content\sounds\impact_water.mp3",             "Sounds.Empty.mp3"    }
        });

        public EmojiModPresetTask EmojiFontTask { get; } = new();

        public EnumModPresetTask<Enums.CursorType> CursorTypeTask { get; } = new("CursorType", new()
        {
            {
                Enums.CursorType.From2006, new()
                {
                    { @"content\textures\Cursors\KeyboardMouse\ArrowCursor.png",    "Cursor.From2006.ArrowCursor.png"    },
                    { @"content\textures\Cursors\KeyboardMouse\ArrowFarCursor.png", "Cursor.From2006.ArrowFarCursor.png" }
                }
            },
            {
                Enums.CursorType.From2013, new()
                {
                    { @"content\textures\Cursors\KeyboardMouse\ArrowCursor.png",    "Cursor.From2013.ArrowCursor.png"    },
                    { @"content\textures\Cursors\KeyboardMouse\ArrowFarCursor.png", "Cursor.From2013.ArrowFarCursor.png" }
                }
            }
        });

        public FontModPresetTask TextFontTask { get; } = new();

        public ModsViewModel() => ReloadCursorSets();

        private void OpenCompatSettings()
        {
            string path = new RobloxPlayerData().ExecutablePath;

            if (File.Exists(path))
                PInvoke.SHObjectProperties(HWND.Null, SHOP_TYPE.SHOP_FILEPATH, path, "Compatibility");
            else
                Frontend.ShowMessageBox(Strings.Common_RobloxNotInstalled, MessageBoxImage.Error);

        }
    }
}
