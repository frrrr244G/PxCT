using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PxCT.Models;
using Color = System.Drawing.Color;
using Point = System.Drawing.Point;

namespace PxCT
{
    internal class MainViewModel : BindableBase
    {
        private const int TileSize = 512;
        private const string TileBaseUrl = "https://pixelcanvas.io/tile";
        private const string MinimapJsonFilename = "Templates/Minimap/templates.json";

        private Canvas _canvas;
        private ImageSource _compareImage;
        private bool _isGridShown = true;
        private bool _isLoading;
        private bool _onlyShowDamages;
        private string _searchText;
        private Template _selectedTemplate;
        private IEnumerable<Template> _templatesFiltered;

        public MainViewModel()
        {
            Initialize();
        }

        public ImageSource CompareImage
        {
            get => _compareImage;
            private set => SetProperty(value, ref _compareImage);
        }

        public bool IsGridShown
        {
            get => _isGridShown;
            set => SetProperty(value, ref _isGridShown);
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(value, ref _isLoading);
        }

        public bool OnlyShowDamages
        {
            get => _onlyShowDamages;
            set { SetProperty(value, ref _onlyShowDamages); RefreshFilteredTemplates(); }
        }

        public string SearchText
        {
            get => _searchText;
            set { SetProperty(value, ref _searchText); RefreshFilteredTemplates(); }
        }

        public Template SelectedTemplate
        {
            get => _selectedTemplate;
            set
            {
                SetProperty(value, ref _selectedTemplate);
                if (value != null) DrawComparison();
                (RefreshTemplateCommand as DelegateCommand)?.RaiseCanExecuteChanged();
                (CopyLinkCommand as DelegateCommand)?.RaiseCanExecuteChanged();
            }
        }

        public IEnumerable<Template> Templates { get; private set; }

        public IEnumerable<Template> TemplatesFiltered
        {
            get => _templatesFiltered;
            set => SetProperty(value, ref _templatesFiltered);
        }

        public ICommand CopyLinkCommand { get; private set; }
        public ICommand CreateJsonCommand { get; private set; }
        public ICommand RefreshTemplateCommand { get; private set; }

        private void ExecuteCopyLink(object obj)
        {
            Clipboard.SetText($"https://pixelcanvas.io/@{SelectedTemplate.Area.X},{SelectedTemplate.Area.Y}");
        }

        private async void ExecuteCreateJson(object obj)
        {
            var minimapTemplates = Templates.Select(o => new MinimapTemplate
            {
                filename = Path.GetFileName(o.Filename),
                x = o.Area.X,
                y = o.Area.Y,
                width = o.Area.Width,
                height = o.Area.Height
            });

            await using var stream = File.Create(MinimapJsonFilename);
            await System.Text.Json.JsonSerializer.SerializeAsync(stream, minimapTemplates);
            MessageBox.Show("JSON-file created.", "Done", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void ExecuteRefreshTemplate(object obj)
        {
            try
            {
                IsLoading = true;
                await LoadCanvasForTemplateAsync(SelectedTemplate);
                MarkErrors(SelectedTemplate);
                DrawComparison();
                OnPropertyChanged(nameof(Templates));
            }
            finally { IsLoading = false; }
        }

        private async Task LoadCanvasForTemplateAsync(Template template)
        {
            var area = template.Area;

            int minTileX = (int)Math.Floor(area.X / (double)TileSize) * TileSize;
            int maxTileX = (int)Math.Floor((area.X + area.Width) / (double)TileSize) * TileSize;
            int minTileY = (int)Math.Floor(area.Y / (double)TileSize) * TileSize;
            int maxTileY = (int)Math.Floor((area.Y + area.Height) / (double)TileSize) * TileSize;

            int width = maxTileX - minTileX + TileSize;
            int height = maxTileY - minTileY + TileSize;
            _canvas = new Canvas
            {
                Pixels = new int[width, height],
                ChunkOffset = new Point(minTileX, minTileY)
            };

            for (int x = minTileX; x <= maxTileX; x += TileSize)
            {
                for (int y = minTileY; y <= maxTileY; y += TileSize)
                {
                    await LoadTileAsync(x, y);
                }
            }
        }

        private async Task LoadTileAsync(int tileX, int tileY)
        {
            string url = $"{TileBaseUrl}/{tileX}/{tileY}.png";
            try
            {
                using var client = new HttpClient();
                using var stream = await client.GetStreamAsync(url);
                using var bmp = new Bitmap(stream);

                int offsetX = tileX - _canvas.ChunkOffset.X;
                int offsetY = tileY - _canvas.ChunkOffset.Y;

                for (int x = 0; x < bmp.Width; x++)
                {
                    for (int y = 0; y < bmp.Height; y++)
                    {
                        var px = bmp.GetPixel(x, y);
                        _canvas.Pixels[x + offsetX, y + offsetY] = px.ToColorId();
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"Tile no encontrado: {url} ({ex.Message})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error descargando tile {url}: {ex.Message}");
            }
        }

        private void InitializeCommands()
        {
            RefreshTemplateCommand = new DelegateCommand(ExecuteRefreshTemplate, o => SelectedTemplate != null);
            CreateJsonCommand = new DelegateCommand(ExecuteCreateJson);
            CopyLinkCommand = new DelegateCommand(ExecuteCopyLink, o => SelectedTemplate != null);
        }

        private void Initialize()
        {
            InitializeCommands();
            LoadTemplates();
        }

        private void LoadTemplates()
        {
            var files = Directory.GetFiles("templates", "*.png");
            var templates = new ConcurrentBag<Template>();

            Parallel.ForEach(files, file =>
            {
                try
                {
                    var nameParts = Path.GetFileNameWithoutExtension(file).Split('_');
                    if (nameParts.Length != 3) throw new ArgumentException("Invalid filename");

                    var bmp = new Bitmap(file);
                    var imageSource = bmp.ToImageSource();
                    imageSource.Freeze();

                    var template = new Template
                    {
                        Filename = file,
                        Name = nameParts[0].AddSpacesBeforeUppercase(),
                        Area = new Rectangle(int.Parse(nameParts[1]), int.Parse(nameParts[2]), bmp.Width, bmp.Height),
                        Pixels = new int[bmp.Width, bmp.Height],
                        Errors = new bool[bmp.Width, bmp.Height],
                        Image = imageSource
                    };

                    for (int x = 0; x < bmp.Width; x++)
                        for (int y = 0; y < bmp.Height; y++)
                            template.Pixels[x, y] = bmp.GetPixel(x, y).ToColorId();

                    templates.Add(template);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to load template {file}:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            });

            Templates = new ObservableCollection<Template>(templates.OrderBy(t => t.Name));
            TemplatesFiltered = Templates;
        }

        private void MarkErrors(Template template)
        {
            int errorCount = 0;
            int pixelCount = 0;

            for (int x = 0; x < template.Pixels.GetLength(0); x++)
            {
                for (int y = 0; y < template.Pixels.GetLength(1); y++)
                {
                    int tx = x + template.Area.X - _canvas.ChunkOffset.X;
                    int ty = y + template.Area.Y - _canvas.ChunkOffset.Y;

                    var target = template.Pixels[x, y];
                    var current = _canvas.Pixels[tx, ty];
                    var hasError = target > -1 && target != current;
                    template.Errors[x, y] = hasError;

                    pixelCount += (target > -1 ? 1 : 0);
                    errorCount += (hasError ? 1 : 0);
                }
            }

            template.PixelCount = pixelCount;
            template.ErrorCount = errorCount;
        }

        private void DrawComparison()
        {
            var w = SelectedTemplate.Pixels.GetLength(0);
            var h = SelectedTemplate.Pixels.GetLength(1);
            var isHorizontal = w * 9 <= h * 16;
            var bmp = new Bitmap(isHorizontal ? w * 2 : w, isHorizontal ? h : h * 2);

            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    var color = CanvasColor.ConvertIdToColor(SelectedTemplate.Pixels[x, y]);
                    bmp.SetPixel(x, y, color);
                    var colorError = SelectedTemplate.Errors[x, y] ? Color.Red : color.ToGrayScale();
                    bmp.SetPixel(isHorizontal ? x + w : x, isHorizontal ? y : y + h, colorError);
                }
            }

            CompareImage = bmp.ToImageSource();
        }

        private void FindTemplateErrors()
        {
            foreach (var template in Templates) MarkErrors(template);
        }

        private void RefreshFilteredTemplates()
        {
            var filtered = string.IsNullOrWhiteSpace(SearchText) ? Templates : Templates.Where(t => t.Name.ToLower().Contains(SearchText.ToLower()));
            if (OnlyShowDamages)
                filtered = filtered.Where(t => t.HasErrors);
            TemplatesFiltered = filtered.ToList();
        }
    }
}
