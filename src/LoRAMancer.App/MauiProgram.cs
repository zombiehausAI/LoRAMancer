using LoRAMancer.App.Engines;
using LoRAMancer.App.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
using MudBlazor.Services;

namespace LoRAMancer.App;

public static class MauiProgram {
    public static MauiApp CreateMauiApp() {
#if WINDOWS
        // Unpackaged apps installed to Program Files fail WebView2 initialization if user data folder
        // defaults to application directory (Access Denied / E_ACCESSDENIED), causing a blank window.
        try {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string webView2Folder = Path.Combine(localAppData, "LoRAMancer", "WebView2");
            Directory.CreateDirectory(webView2Folder);
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", webView2Folder);
        } catch {
            // Fallback gracefully
        }
#endif

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts => {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

#if WINDOWS
        builder.ConfigureLifecycleEvents(events => {
            events.AddWindows(windows => {
                windows.OnWindowCreated(window => {
                    var trayService = IPlatformApplication.Current?.Services.GetService<SystemTrayService>();
                    trayService?.Initialize(window);
                });
            });
        });
#endif

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Services.AddMudServices();

        builder.Services.AddSingleton<HttpClient>();
        builder.Services.AddSingleton<ProcessRunner>();
        builder.Services.AddSingleton<ModelArchitectureRegistry>();
        builder.Services.AddSingleton<SettingsService>();
        builder.Services.AddSingleton<ThemeService>();
        builder.Services.AddSingleton<AuthTokenManagerService>();
        builder.Services.AddSingleton<AiToolkitSetupService>();
        builder.Services.AddSingleton<CivitaiService>();
        builder.Services.AddSingleton<HuggingFaceService>();
        builder.Services.AddSingleton<DanbooruTagService>();
        builder.Services.AddSingleton<LoraMetadataAggregatorService>();
        builder.Services.AddSingleton<SafeTensorsMetadataReader>();
        builder.Services.AddSingleton<AiToolkitConfigBuilder>();
        builder.Services.AddSingleton<AmdVenvProvisioner>();
        builder.Services.AddSingleton<TrainingRunnerService>();
        builder.Services.AddSingleton<TrainingEstimationService>();
        builder.Services.AddSingleton<DatasetInspectorService>();
        builder.Services.AddSingleton<NativeFileDialogService>();
        builder.Services.AddSingleton<PluginManagerService>();
        builder.Services.AddSingleton<AutoUpdateService>();
        builder.Services.AddSingleton<PublicTunnelService>();
        builder.Services.AddSingleton<NetworkServerService>();
        builder.Services.AddSingleton<RemoteTrainingClientService>();
        builder.Services.AddSingleton<LoraHistoryService>();
        builder.Services.AddSingleton<LoraDatabaseService>();
        builder.Services.AddSingleton<LoraLibraryService>();
        builder.Services.AddSingleton<LoraUpdaterService>();
        builder.Services.AddSingleton<ComfyUiService>();
        builder.Services.AddSingleton<DatasetCuratorService>();
        builder.Services.AddSingleton<LoraSurgeryService>();
        builder.Services.AddSingleton<LoraDiffService>();
        builder.Services.AddSingleton<SemanticCollisionService>();
        builder.Services.AddSingleton<LoraBenchmarkService>();
        builder.Services.AddSingleton<OverbakeRadarService>();
        builder.Services.AddSingleton<LoraEchoHunterService>();
        builder.Services.AddSingleton<LoraDeAnonymizerService>();
        builder.Services.AddSingleton<LoraChopShopService>();
        builder.Services.AddSingleton<StudioSessionService>();
        builder.Services.AddSingleton<SystemTrayService>();
        builder.Services.AddSingleton<PostForgeShowcaseService>();
        builder.Services.AddSingleton<ShowcaseThumbnailService>();
        builder.Services.AddSingleton<ModusFlowPromptService>();
        builder.Services.AddSingleton<TrainingRecipeService>();
        builder.Services.AddSingleton<ImageHarvesterService>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
