using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameTranslate.Models;
using GameTranslate.Native;
using GameTranslate.Services;
using Bitmap = System.Drawing.Bitmap;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using ComboBox = System.Windows.Controls.ComboBox;
using FontFamily = System.Windows.Media.FontFamily;

namespace GameTranslate.Windows;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly ScreenCaptureService _captureService;
    private readonly WindowsOcrService _ocrService;
    private readonly TranslationService _translationService;
    private readonly SubtitlePipeline _pipeline;
    private readonly SubtitleOverlayWindow _overlayWindow;
    private readonly GlobalHotkeyManager _hotkeyManager;
    private TrayIconHelper? _trayIcon;

    private AppSettings _settings;
    private bool _isLoadingUi = true;
    private bool _isExiting;
    private bool _hasShownTrayTip;

    public MainWindow()
    {
        InitializeComponent();

        _settingsService = new SettingsService();
        _settings = _settingsService.Load();

        _captureService = new ScreenCaptureService();
        _ocrService = new WindowsOcrService();
        _translationService = new TranslationService();
        _pipeline = new SubtitlePipeline(_captureService, _ocrService, _translationService);
        _overlayWindow = new SubtitleOverlayWindow();
        _hotkeyManager = new GlobalHotkeyManager();

        WireEvents();
        PopulateOcrLanguages();
        LoadSettingsIntoUi();
        _isLoadingUi = false;

        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Initialize overlay window cleanly (stays transparent until subtitles appear)
        _overlayWindow.Show();
        _overlayWindow.ApplySettings(_settings);
        UpdateStealthBadge();

        // Pre-warm Bing LLM session token in background so hybrid translations have zero auth lag
        _translationService.PrewarmInBackground();

        // Initialize Windows OCR right away so user sees the active recognizer
        bool ocrOk = _pipeline.InitializeOcr(_settings.PreferredOcrLanguage);
        OcrInfoText.Text = ocrOk
            ? $"OCR Nativo Windows: {_pipeline.ActiveOcrLanguage}  •  Sem instalação externa"
            : "Aviso: Nenhum pacote de OCR do Windows encontrado.";

        // Register global hotkeys (Alt+T, Alt+H, Alt+R, Alt+O)
        _hotkeyManager.Attach(this);
        _hotkeyManager.Register(GlobalHotkeyManager.HotkeyToggleTranslate, Win32.MOD_ALT, (uint)System.Windows.Forms.Keys.T, () => Dispatcher.Invoke(ToggleTranslation));
        _hotkeyManager.Register(GlobalHotkeyManager.HotkeyToggleOverlay, Win32.MOD_ALT, (uint)System.Windows.Forms.Keys.H, () => Dispatcher.Invoke(ToggleOverlayVisibility));
        _hotkeyManager.Register(GlobalHotkeyManager.HotkeySelectRegion, Win32.MOD_ALT, (uint)System.Windows.Forms.Keys.R, () => Dispatcher.Invoke(OpenRegionSelector));
        _hotkeyManager.Register(GlobalHotkeyManager.HotkeyForceTranslate, Win32.MOD_ALT, (uint)System.Windows.Forms.Keys.O, () => Dispatcher.Invoke(TriggerImmediateCapture));

        // Create System Tray icon
        _trayIcon = new TrayIconHelper(
            onOpenWindow: () => Dispatcher.Invoke(RestoreFromTray),
            onToggleRun: () => Dispatcher.Invoke(ToggleTranslation),
            onSelectRegion: () => Dispatcher.Invoke(OpenRegionSelector),
            onToggleOverlay: () => Dispatcher.Invoke(ToggleOverlayVisibility),
            onExit: () => Dispatcher.Invoke(ExitApplication));
        _trayIcon.UpdateState(_pipeline.IsRunning, _overlayWindow.IsUserHidden);

        UpdateMiniPreview();
    }

    private void WireEvents()
    {
        _pipeline.SubtitleUpdated += args =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                _overlayWindow.ShowSubtitle(args.TranslatedText, args.OriginalText, _settings.ShowOriginalTextSubtle);
                LastOriginalText.Text = args.OriginalText;
                LastTranslatedText.Text = args.TranslatedText;
                string cacheNote = args.FromCache ? "Cache" : args.ProviderUsed;
                MetricsText.Text = $"{args.LatencyMs} ms  •  {cacheNote}";
            });
        };

        _pipeline.SubtitleCleared += () =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                _overlayWindow.ClearSubtitle();
            });
        };

        _pipeline.StatusMessage += msg =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                MetricsText.Text = msg;
            });
        };

        _pipeline.ErrorOccurred += err =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                MetricsText.Text = err;
            });
        };

        _overlayWindow.PositionLocked += bounds =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                _settings.OverlayPosition = OverlayPositionMode.CustomFree;
                _settings.OverlayRegion = bounds;
                _isLoadingUi = true;
                SelectComboByTag(OverlayPosCombo, nameof(OverlayPositionMode.CustomFree));
                _isLoadingUi = false;
                RepositionButton.Content = "📐  Reposicionar Legenda";
                _settingsService.Save(_settings);
                _overlayWindow.ApplySettings(_settings);
            });
        };
    }

    private void PopulateOcrLanguages()
    {
        OcrLangCombo.Items.Clear();
        OcrLangCombo.Items.Add(new ComboBoxItem { Content = "Automático (prefere Inglês, usa PT-BR se necessário)", Tag = "auto" });
        foreach (var (tag, display) in WindowsOcrService.GetAvailableLanguages())
        {
            OcrLangCombo.Items.Add(new ComboBoxItem { Content = display, Tag = tag });
        }
    }

    private void LoadSettingsIntoUi()
    {
        ColorHexBox.Text = _settings.SubtitleColorHex;
        SelectComboByContent(FontFamilyCombo, _settings.FontFamily);
        FontSizeSlider.Value = Math.Clamp(_settings.FontSize, 16, 60);
        FontSizeLabel.Text = $"{(int)FontSizeSlider.Value} px";
        BoldCheck.IsChecked = _settings.FontBold;
        ItalicCheck.IsChecked = _settings.FontItalic;
        ShadowCheck.IsChecked = _settings.DropShadowEnabled;

        OutlineSlider.Value = Math.Clamp(_settings.OutlineThickness, 0, 8);
        OutlineLabel.Text = $"{OutlineSlider.Value:0.0} px";

        BackdropSlider.Value = Math.Round(Math.Clamp(_settings.BackdropOpacity, 0, 1) * 100);
        BackdropLabel.Text = $"{(int)BackdropSlider.Value}%";

        SelectComboByTag(OverlayPosCombo, _settings.OverlayPosition.ToString());
        CompactMenuCheck.IsChecked = _settings.CompactMenuLines;
        ShowOriginalCheck.IsChecked = _settings.ShowOriginalTextSubtle;

        RegionSummaryText.Text = _settings.CaptureRegion.ToString();
        SelectComboByTag(OcrFilterCombo, _settings.OcrFilter.ToString());
        LumaSlider.Value = Math.Clamp(_settings.WhiteTextLuminanceThreshold, 110, 240);
        LumaLabel.Text = $"{(int)LumaSlider.Value}";
        IntervalSlider.Value = Math.Clamp(_settings.CaptureIntervalMs, 100, 1000);
        IntervalLabel.Text = $"{(int)IntervalSlider.Value} ms";
        SelectComboByTag(OcrLangCombo, _settings.PreferredOcrLanguage);
        ExcludeCaptureCheck.IsChecked = _settings.ExcludeOverlayFromCapture;
        AutoExcludeTitleBarCheck.IsChecked = _settings.AutoExcludeWindowTitleBar;
        AutoCorrectOcrCheck.IsChecked = _settings.AutoCorrectOcrTypos;
        MinimizeToTrayCheck.IsChecked = _settings.MinimizeToTrayOnClose;

        SelectComboByTag(ProviderCombo, _settings.Provider.ToString());
        GamingGlossaryCheck.IsChecked = _settings.UseGamingGlossary;
        SelectComboByTag(SourceLangCombo, _settings.SourceLanguage);
        SelectComboByTag(TargetLangCombo, _settings.TargetLanguage);
        DeepLKeyBox.Password = _settings.DeepLApiKey;
        OpenAiEndpointBox.Text = _settings.OpenAiEndpoint;
        OpenAiModelBox.Text = _settings.OpenAiModel;
        OpenAiKeyBox.Password = _settings.OpenAiApiKey;
        UpdateTranslatorPanelsVisibility();
    }

    private void SaveAndApplySettings()
    {
        if (_isLoadingUi)
            return;

        _settings.SubtitleColorHex = string.IsNullOrWhiteSpace(ColorHexBox.Text) ? "#FFD800" : ColorHexBox.Text.Trim();
        if (FontFamilyCombo.SelectedItem is ComboBoxItem ff)
            _settings.FontFamily = ff.Content?.ToString() ?? "Arial";
        _settings.FontSize = FontSizeSlider.Value;
        _settings.FontBold = BoldCheck.IsChecked == true;
        _settings.FontItalic = ItalicCheck.IsChecked == true;
        _settings.DropShadowEnabled = ShadowCheck.IsChecked == true;
        _settings.OutlineThickness = OutlineSlider.Value;
        _settings.BackdropOpacity = BackdropSlider.Value / 100.0;
        if (OverlayPosCombo.SelectedItem is ComboBoxItem posItem &&
            Enum.TryParse<OverlayPositionMode>(posItem.Tag?.ToString(), out var posMode))
            _settings.OverlayPosition = posMode;

        _settings.CompactMenuLines = CompactMenuCheck.IsChecked == true;
        _settings.ShowOriginalTextSubtle = ShowOriginalCheck.IsChecked == true;

        if (OcrFilterCombo.SelectedItem is ComboBoxItem filterItem &&
            Enum.TryParse<OcrFilterMode>(filterItem.Tag?.ToString(), out var mode))
            _settings.OcrFilter = mode;

        _settings.WhiteTextLuminanceThreshold = (int)LumaSlider.Value;
        _settings.CaptureIntervalMs = (int)IntervalSlider.Value;

        if (OcrLangCombo.SelectedItem is ComboBoxItem langItem)
            _settings.PreferredOcrLanguage = langItem.Tag?.ToString() ?? "auto";

        _settings.ExcludeOverlayFromCapture = ExcludeCaptureCheck.IsChecked == true;
        _settings.AutoExcludeWindowTitleBar = AutoExcludeTitleBarCheck.IsChecked == true;
        _settings.AutoCorrectOcrTypos = AutoCorrectOcrCheck.IsChecked == true;
        _settings.MinimizeToTrayOnClose = MinimizeToTrayCheck.IsChecked == true;

        if (ProviderCombo.SelectedItem is ComboBoxItem provItem &&
            Enum.TryParse<TranslatorProvider>(provItem.Tag?.ToString(), out var provider))
            _settings.Provider = provider;

        _settings.UseGamingGlossary = GamingGlossaryCheck.IsChecked == true;

        if (SourceLangCombo.SelectedItem is ComboBoxItem srcItem)
            _settings.SourceLanguage = srcItem.Tag?.ToString() ?? "en";

        if (TargetLangCombo.SelectedItem is ComboBoxItem tgtItem)
            _settings.TargetLanguage = tgtItem.Tag?.ToString() ?? "pt-BR";

        _settings.DeepLApiKey = DeepLKeyBox.Password?.Trim() ?? string.Empty;
        _settings.OpenAiEndpoint = OpenAiEndpointBox.Text?.Trim() ?? string.Empty;
        _settings.OpenAiModel = OpenAiModelBox.Text?.Trim() ?? string.Empty;
        _settings.OpenAiApiKey = OpenAiKeyBox.Password?.Trim() ?? string.Empty;

        _settingsService.Save(_settings);
        _overlayWindow.ApplySettings(_settings);
        UpdateMiniPreview();
        UpdateStealthBadge();
    }

    private void UpdateMiniPreview()
    {
        try
        {
            MiniPreviewSubtitle.FontFamily = new FontFamily(_settings.FontFamily);
            MiniPreviewSubtitle.FontSize = Math.Clamp(_settings.FontSize * 0.72, 14, 34);
            MiniPreviewSubtitle.FontWeight = _settings.FontBold ? FontWeights.Bold : FontWeights.Normal;
            MiniPreviewSubtitle.FontStyle = _settings.FontItalic ? FontStyles.Italic : FontStyles.Normal;
            MiniPreviewSubtitle.StrokeThickness = Math.Clamp(_settings.OutlineThickness * 0.85, 0, 7);
            MiniPreviewSubtitle.DropShadowEnabled = _settings.DropShadowEnabled;
            MiniPreviewSubtitle.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_settings.SubtitleColorHex));

            byte alpha = (byte)Math.Round(Math.Clamp(_settings.BackdropOpacity, 0, 1) * 255);
            MiniPreviewBackdrop.Background = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));
        }
        catch
        {
            // Ignore transient invalid hex while user is typing
        }
    }

    private void UpdateStealthBadge()
    {
        if (_overlayWindow.IsExcludedFromCaptureActive)
        {
            StealthBadge.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x2B, 0x22));
            StealthBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x6B, 0x43));
            StealthBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(0x7E, 0xE2, 0xA0));
            StealthBadgeText.Text = "🛡 Sobreposição Inteligente";
        }
        else
        {
            StealthBadge.Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x24, 0x18));
            StealthBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(0x7A, 0x5A, 0x24));
            StealthBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xC1, 0x6C));
            StealthBadgeText.Text = "⚠ Modo Clássico (posicione fora da captura)";
        }
    }

    private void UpdateRunningUiState()
    {
        bool running = _pipeline.IsRunning;
        StartStopButton.Content = running ? "⏸  Pausar Tradução (Alt+T)" : "▶  Iniciar Tradução (Alt+T)";
        StatusPillText.Text = running ? "● TRADUZINDO" : "● PAUSADO";
        StatusPillText.Foreground = running
            ? new SolidColorBrush(Color.FromRgb(0x6C, 0xF0, 0x9C))
            : new SolidColorBrush(Color.FromRgb(0xFF, 0xD8, 0x00));
        StatusPill.Background = running
            ? new SolidColorBrush(Color.FromRgb(0x16, 0x2B, 0x1E))
            : new SolidColorBrush(Color.FromRgb(0x2B, 0x24, 0x16));
        StatusPill.BorderBrush = running
            ? new SolidColorBrush(Color.FromRgb(0x2D, 0x7A, 0x4B))
            : new SolidColorBrush(Color.FromRgb(0x8A, 0x6C, 0x1F));

        _trayIcon?.UpdateState(running, _overlayWindow.IsUserHidden);
    }

    private void ToggleTranslation()
    {
        if (_overlayWindow.IsEditMode)
            _overlayWindow.SetEditMode(false);

        if (_pipeline.IsRunning)
        {
            _pipeline.Stop();
            _overlayWindow.ClearSubtitle();
        }
        else
        {
            _overlayWindow.ApplySettings(_settings);
            _pipeline.Start(() => _settings);
        }

        UpdateRunningUiState();
    }

    private void ToggleOverlayVisibility()
    {
        bool nextHidden = !_overlayWindow.IsUserHidden;
        _overlayWindow.SetUserHidden(nextHidden);
        _trayIcon?.UpdateState(_pipeline.IsRunning, nextHidden);
        MetricsText.Text = nextHidden ? "Legenda oculta (Alt+H para mostrar)" : "Legenda visível";
    }

    private void OpenRegionSelector()
    {
        bool wasRunning = _pipeline.IsRunning;
        if (wasRunning)
            _pipeline.Stop();

        if (_overlayWindow.IsEditMode)
            _overlayWindow.SetEditMode(false);

        _overlayWindow.ClearSubtitle();

        var selector = new RegionSelectorWindow(_settings.AutoExcludeWindowTitleBar);
        selector.ShowDialog();
        if (selector.SelectedRegion is { IsValid: true } newRegion)
        {
            _settings.CaptureRegion = newRegion;

            RegionSummaryText.Text = newRegion.ToString();
            _settingsService.Save(_settings);
            _overlayWindow.ApplySettings(_settings);
            MetricsText.Text = $"Área atualizada (sem barra de título): {newRegion}";
        }

        if (wasRunning)
            _pipeline.Start(() => _settings);

        UpdateRunningUiState();
    }

    private async void TriggerImmediateCapture()
    {
        if (_pipeline.IsRunning)
        {
            _pipeline.RequestImmediateCapture();
            MetricsText.Text = "Leitura forçada solicitada...";
            return;
        }

        await RunDiagnosticTestAsync();
    }

    private async Task RunDiagnosticTestAsync()
    {
        TestNowButton.IsEnabled = false;
        MetricsText.Text = "Capturando e lendo texto com OCR...";
        try
        {
            var (previewBmp, ocrText, translated, ms) = await _pipeline.RunSingleDiagnosticCaptureAsync(_settings);
            if (previewBmp != null)
            {
                DiagnosticPreviewImage.Source = ConvertBitmapToImageSource(previewBmp);
                PreviewPlaceholderText.Visibility = Visibility.Collapsed;
                previewBmp.Dispose();
            }

            if (string.IsNullOrWhiteSpace(ocrText))
            {
                LastOriginalText.Text = "(Nenhum texto detectado na área selecionada)";
                LastTranslatedText.Text = "—";
                MetricsText.Text = $"Nenhum texto ({ms} ms) — tente ajustar a área ou o filtro";
            }
            else
            {
                LastOriginalText.Text = ocrText;
                LastTranslatedText.Text = translated;
                MetricsText.Text = $"Teste concluído em {ms} ms";
            }
        }
        catch (Exception ex)
        {
            MetricsText.Text = $"Erro no teste: {ex.Message}";
        }
        finally
        {
            TestNowButton.IsEnabled = true;
        }
    }

    private void StartStopButton_Click(object sender, RoutedEventArgs e) => ToggleTranslation();

    private void SelectRegionButton_Click(object sender, RoutedEventArgs e) => OpenRegionSelector();

    private void RepositionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_overlayWindow.IsEditMode)
        {
            _overlayWindow.ApplySettings(_settings);
            _overlayWindow.SetEditMode(true);
            RepositionButton.Content = "🔒  Travar Posição";
        }
        else
        {
            _overlayWindow.SetEditMode(false);
            RepositionButton.Content = "📐  Reposicionar Legenda";
        }
    }

    private async void TestNowButton_Click(object sender, RoutedEventArgs e) => await RunDiagnosticTestAsync();

    private void PreviewOnScreenButton_Click(object sender, RoutedEventArgs e)
    {
        _overlayWindow.ApplySettings(_settings);
        _overlayWindow.SetUserHidden(false);
        _overlayWindow.ShowPreviewSample("Precisamos encontrar a chave antes do nascer do sol.");
        MetricsText.Text = "Prévia da legenda amarela exibida na tela!";
    }

    private void PresetBottomRegion_Click(object sender, RoutedEventArgs e)
    {
        var (w, h) = Win32.GetPrimaryScreenSize();
        _settings.CaptureRegion = AppSettings.CreateDefaultBottomSubtitleRect(0, 0, w, h);

        RegionSummaryText.Text = _settings.CaptureRegion.ToString();
        _settingsService.Save(_settings);
        _overlayWindow.ApplySettings(_settings);
        MetricsText.Text = $"Área padrão definida: {_settings.CaptureRegion}";
    }

    private void ColorPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string hex)
        {
            ColorHexBox.Text = hex;
            SaveAndApplySettings();
        }
    }

    private void StyleInput_Changed(object sender, EventArgs e) => SaveAndApplySettings();

    private void StyleSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoadingUi) return;
        FontSizeLabel.Text = $"{(int)FontSizeSlider.Value} px";
        OutlineLabel.Text = $"{OutlineSlider.Value:0.0} px";
        BackdropLabel.Text = $"{(int)BackdropSlider.Value}%";
        SaveAndApplySettings();
    }

    private void StyleCheck_Changed(object sender, RoutedEventArgs e) => SaveAndApplySettings();

    private void OcrSetting_Changed(object sender, SelectionChangedEventArgs e) => SaveAndApplySettings();

    private void OcrSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoadingUi) return;
        LumaLabel.Text = $"{(int)LumaSlider.Value}";
        IntervalLabel.Text = $"{(int)IntervalSlider.Value} ms";
        SaveAndApplySettings();
    }

    private void OcrCheck_Changed(object sender, RoutedEventArgs e) => SaveAndApplySettings();

    private void TranslatorSetting_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateTranslatorPanelsVisibility();
        SaveAndApplySettings();
    }

    private void TranslatorText_Changed(object sender, TextChangedEventArgs e) => SaveAndApplySettings();

    private void TranslatorPassword_Changed(object sender, RoutedEventArgs e) => SaveAndApplySettings();

    private void ClearCacheButton_Click(object sender, RoutedEventArgs e)
    {
        _translationService.ClearCache();
        MetricsText.Text = "Cache de traduções limpo.";
    }

    private void UpdateTranslatorPanelsVisibility()
    {
        if (ProviderCombo?.SelectedItem is not ComboBoxItem item) return;
        string tag = item.Tag?.ToString() ?? string.Empty;
        DeepLPanel.Visibility = tag == nameof(TranslatorProvider.DeepLApi) ? Visibility.Visible : Visibility.Collapsed;
        OpenAiPanel.Visibility = tag == nameof(TranslatorProvider.OpenAiCompatible) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isExiting && _settings.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            Hide();
            if (!_hasShownTrayTip)
            {
                _hasShownTrayTip = true;
                _trayIcon?.ShowBalloon("GameTranslate em segundo plano", "Use Alt+T para iniciar/pausar ou clique duas vezes no ícone perto do relógio.");
            }
            return;
        }

        CleanupResources();
    }

    private void ExitApplication()
    {
        _isExiting = true;
        CleanupResources();
        System.Windows.Application.Current.Shutdown();
    }

    private void CleanupResources()
    {
        _pipeline.Dispose();
        _hotkeyManager.Dispose();
        _translationService.Dispose();
        _trayIcon?.Dispose();
        _trayIcon = null;
        if (_overlayWindow.IsLoaded)
            _overlayWindow.Close();
    }

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (object item in combo.Items)
        {
            if (item is ComboBoxItem cbi &&
                string.Equals(cbi.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = cbi;
                return;
            }
        }
        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    private static void SelectComboByContent(ComboBox combo, string content)
    {
        foreach (object item in combo.Items)
        {
            if (item is ComboBoxItem cbi &&
                string.Equals(cbi.Content?.ToString(), content, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = cbi;
                return;
            }
        }
        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    private static BitmapImage ConvertBitmapToImageSource(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }
}
