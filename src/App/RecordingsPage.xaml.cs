using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DriveDeck.App.Services;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace DriveDeck.App
{
    public sealed partial class RecordingsPage : Page
    {
        private sealed class Clip
        {
            public StorageFile Video;
            public StorageFolder Folder;
            public string Title;
        }

        private const int MaxLoopItems = 300;
        private List<Clip> _playlist = new List<Clip>();
        private int _playIndex;

        public RecordingsPage() { InitializeComponent(); }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await LoadAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            Media.Stop();
            Media.Source = null;
            base.OnNavigatedFrom(e);
        }

        private static string Size(ulong b) { return b > 1073741824 ? (b / 1073741824.0).ToString("0.0") + " GB" : (b / 1048576.0).ToString("0") + " MB"; }

        private async Task LoadAsync()
        {
            EventList.Items.Clear();
            LoopList.Items.Clear();
            var events = AppHub.Dashcam.EventsFolder;
            var loop = AppHub.Dashcam.LoopFolder;
            if (events == null || loop == null)
            {
                try
                {
                    var root = await KnownFolders.VideosLibrary.CreateFolderAsync("DriveDeck", CreationCollisionOption.OpenIfExists);
                    events = await root.CreateFolderAsync("Events", CreationCollisionOption.OpenIfExists);
                    loop = await root.CreateFolderAsync("Loop", CreationCollisionOption.OpenIfExists);
                }
                catch { return; }
            }

            var folders = (await events.GetFoldersAsync()).OrderByDescending(f => f.Name).ToList();
            foreach (var f in folders)
            {
                var videos = (await f.GetFilesAsync()).Where(x => x.FileType.Equals(".mp4", StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.Name).ToList();
                ulong total = 0;
                foreach (var file in await f.GetFilesAsync()) total += (await file.GetBasicPropertiesAsync()).Size;
                var clips = videos.Select(v => new Clip { Video = v, Folder = f, Title = f.Name + " / " + v.Name }).ToList();
                EventList.Items.Add(MakeItem(EventTitle(f.Name), videos.Count + " video(s), " + Size(total), clips, f));
            }
            EventsEmpty.Visibility = folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var loopVideos = (await loop.GetFilesAsync()).Where(x => x.FileType.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                                                          .OrderByDescending(x => x.Name).Take(MaxLoopItems).ToList();
            var loopClips = loopVideos.Select(v => new Clip { Video = v, Folder = loop, Title = v.Name }).ToList();
            for (int i = 0; i < loopVideos.Count; i++)
            {
                var props = await loopVideos[i].GetBasicPropertiesAsync();
                LoopList.Items.Add(MakeItem(loopVideos[i].DisplayName, Size(props.Size), loopClips.Skip(i).Concat(loopClips.Take(i)).ToList(), null));
            }
            LoopEmpty.Visibility = loopVideos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string EventTitle(string folderName)
        {
            DateTime t;
            string kind = folderName.Contains("_manual") ? "Saved manually" : "Impact";
            return Core.SegmentNaming.TryParse(folderName, out t) ? kind + " – " + t.ToString("d.M.yyyy HH:mm:ss") : folderName;
        }

        private FrameworkElement MakeItem(string title, string detail, List<Clip> clips, StorageFolder deletable)
        {
            var g = new Grid { Margin = new Thickness(0, 6, 0, 6), Tag = clips };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = detail, FontSize = 13, Foreground = new SolidColorBrush(Colors.Gray) });
            g.Children.Add(text);
            if (deletable != null)
            {
                var del = new Button { Content = "Delete", VerticalAlignment = VerticalAlignment.Center };
                del.Click += async (s, e) => await DeleteEventAsync(deletable, title);
                Grid.SetColumn(del, 1);
                g.Children.Add(del);
            }
            return g;
        }

        private async Task DeleteEventAsync(StorageFolder folder, string title)
        {
            var dlg = new ContentDialog
            {
                Title = "Delete incident?",
                Content = new TextBlock { Text = title + "\n\nThe videos are deleted permanently from the phone. Copy them to a PC first if you may need them.", TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Delete permanently", SecondaryButtonText = "Cancel"
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            try { await folder.DeleteAsync(StorageDeleteOption.PermanentDelete); }
            catch (Exception ex) { await new ContentDialog { Title = "Could not delete", Content = ex.Message, PrimaryButtonText = "OK" }.ShowAsync(); }
            await LoadAsync();
        }

        private async void List_ItemClick(object sender, ItemClickEventArgs e)
        {
            var g = e.ClickedItem as FrameworkElement;
            var clips = g != null ? g.Tag as List<Clip> : null;
            if (clips == null || clips.Count == 0) return;
            _playlist = clips;
            _playIndex = 0;
            await PlayAsync();
        }

        private async Task PlayAsync()
        {
            if (_playIndex < 0 || _playIndex >= _playlist.Count) return;
            var clip = _playlist[_playIndex];
            PlayerTitle.Text = clip.Title;
            Player.Visibility = Visibility.Visible;
            var source = MediaSource.CreateFromStorageFile(clip.Video);
            try
            {
                var srt = await clip.Folder.TryGetItemAsync(clip.Video.DisplayName + ".srt") as StorageFile;
                if (srt != null) source.ExternalTimedTextSources.Add(TimedTextSource.CreateFromStream(await srt.OpenReadAsync()));
            }
            catch { }
            var item = new MediaPlaybackItem(source);
            item.TimedMetadataTracksChanged += (s, a) =>
            {
                for (uint i = 0; i < s.TimedMetadataTracks.Count; i++)
                    s.TimedMetadataTracks.SetPresentationMode(i, TimedMetadataTrackPresentationMode.PlatformPresented);
            };
            Media.SetPlaybackSource(item);
        }

        private async void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_playIndex + 1 < _playlist.Count) { _playIndex++; await PlayAsync(); }
        }

        private async void Prev_Click(object sender, RoutedEventArgs e)
        {
            if (_playIndex > 0) { _playIndex--; await PlayAsync(); }
        }

        private void ClosePlayer_Click(object sender, RoutedEventArgs e)
        {
            Media.Stop();
            Media.Source = null;
            Player.Visibility = Visibility.Collapsed;
        }
    }
}
