using LoRAMancer.App.Engines;
using LoRAMancer.App.Services;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;

namespace LoRAMancer.App;

public static class MauiProgram {
    public static MauiApp CreateMauiApp() {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts => {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddMudServices();

        builder.Services.AddSingleton<HttpClient>();
        builder.Services.AddSingleton<ProcessRunner>();
        builder.Services.AddSingleton<ModelArchitectureRegistry>();
        builder.Services.AddSingleton<SettingsService>();
        builder.Services.AddSingleton<AiToolkitSetupService>();
        builder.Services.AddSingleton<CivitaiService>();
        builder.Services.AddSingleton<HuggingFaceService>();
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
        builder.Services.AddSingleton<LoraLibraryService>();
        builder.Services.AddSingleton<LoraUpdaterService>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
