using Spectre.Console;
using Spectre.Console.Cli;
using SyncJob.Database;
using SyncJob.Services;
using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace SyncJob.Commands
{
    // ============================================================================
    // CENTRAL SETUP - Configuración interactiva
    // ============================================================================

    [Description("Configure central synchronization server")]
    public class CentralSetupCommand : AsyncCommand<CentralSetupCommand.Settings>
    {
        public class Settings : CommandSettings
        {
            [Description("Reconfigure existing setup")]
            [CommandOption("--reconfigure")]
            public bool Reconfigure { get; set; }
        }

        public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
        {
            DbManager.Initialize();

            try
            {
                // Verificar si ya está configurado
                var isConfigured = CentralSyncRepository.IsConfigured();

                if (isConfigured && !settings.Reconfigure)
                {
                    var existingSettings = CentralSyncRepository.GetSettings();

                    AnsiConsole.MarkupLine("[yellow]⚠️  Central sync is already configured[/]");
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine($"  Project ID: [cyan]{existingSettings.ProjectId}[/]");
                    AnsiConsole.MarkupLine($"  Server ID: [cyan]{existingSettings.ServerId}[/]");
                    AnsiConsole.MarkupLine($"  Status: {(existingSettings.Enabled ? "[green]Enabled[/]" : "[red]Disabled[/]")}");
                    AnsiConsole.WriteLine();

                    if (!AnsiConsole.Confirm("Reconfigure?", false))
                    {
                        AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
                        return 0;
                    }
                }

                // Banner
                var panel = new Panel(new FigletText("Central Sync").Centered().Color(Color.Aqua))
                    .Border(BoxBorder.Double)
                    .BorderStyle(new Style(Color.Aqua));
                AnsiConsole.Write(panel);
                AnsiConsole.WriteLine();

                AnsiConsole.MarkupLine("[bold]This wizard will configure the connection to PeopleWorks central server[/]");
                AnsiConsole.MarkupLine("where all sync logs and configurations will be centralized.");
                AnsiConsole.WriteLine();

                // Leer configuración desde appsettings.json si existe
                var currentSettings = CentralSyncRepository.GetSettings();
                var projectId = currentSettings.ProjectId;
                var serverId = currentSettings.ServerId;

                // Proyecto ID
                AnsiConsole.MarkupLine("[bold cyan]📋 Project Information[/]");
                AnsiConsole.MarkupLine("────────────────────────────────────────────────────────────────");

                if (string.IsNullOrEmpty(projectId))
                {
                    projectId = AnsiConsole.Prompt(
                        new TextPrompt<string>("[yellow]Project ID:[/]")
                            .DefaultValue("iberofarmacos-prod")
                            .Validate(id =>
                            {
                                if (string.IsNullOrWhiteSpace(id))
                                    return ValidationResult.Error("[red]Project ID is required[/]");
                                return ValidationResult.Success();
                            }));
                }
                else
                {
                    AnsiConsole.MarkupLine($"  • Project ID (configured): [cyan]{projectId}[/]");
                }

                // Server ID
                if (string.IsNullOrEmpty(serverId))
                {
                    serverId = AnsiConsole.Prompt(
                        new TextPrompt<string>("[yellow]Server ID:[/]")
                            .DefaultValue($"{projectId}-server-main")
                            .Validate(id =>
                            {
                                if (string.IsNullOrWhiteSpace(id))
                                    return ValidationResult.Error("[red]Server ID is required[/]");
                                return ValidationResult.Success();
                            }));
                }
                else
                {
                    AnsiConsole.MarkupLine($"  • Server ID (configured): [cyan]{serverId}[/]");
                }

                AnsiConsole.WriteLine();

                // Configuración del servidor central
                AnsiConsole.MarkupLine("[bold cyan]🔌 Central Server Configuration[/]");
                AnsiConsole.MarkupLine("────────────────────────────────────────────────────────────────");

                var serverName = AnsiConsole.Prompt(
                    new TextPrompt<string>(" SQL Server:")
                        .DefaultValue("213.136.72.8"));

                var port = AnsiConsole.Prompt(
                    new TextPrompt<string>(" Port:")
                        .DefaultValue("1433")
                        .AllowEmpty());

                var database = AnsiConsole.Prompt(
                    new TextPrompt<string>(" Database:")
                        .DefaultValue("SyncJobCentralDB"));

                var username = AnsiConsole.Prompt(
                    new TextPrompt<string>(" Username:")
                        .DefaultValue("syncjob_api"));

                var password = AnsiConsole.Prompt(
                    new TextPrompt<string>(" Password:")
                        .Secret());

                AnsiConsole.WriteLine();

                // API Key
                AnsiConsole.MarkupLine("[bold cyan]🔐 API Authentication[/]");
                AnsiConsole.MarkupLine("────────────────────────────────────────────────────────────────");

                var apiKey = AnsiConsole.Prompt(
                    new TextPrompt<string>(" API Key:")
                        .Secret()
                        .AllowEmpty());

                if (string.IsNullOrEmpty(apiKey))
                {
                    apiKey = CentralSyncRepository.GenerateApiKey();
                    AnsiConsole.MarkupLine($" [dim]Generated API Key: {apiKey}[/]");
                }

                AnsiConsole.WriteLine();

                // Construir connection string
                var serverAddress = string.IsNullOrEmpty(port) || port == "1433"
                    ? serverName
                    : $"{serverName},{port}";

                var connectionString = $"Server={serverAddress};Database={database};User Id={username};Password={password};Encrypt=true;TrustServerCertificate=true;Connection Timeout=30;";

                // Validar conexión
                AnsiConsole.Status()
                    .Start("✅ Validating connection...", ctx =>
                    {
                        ctx.Spinner(Spinner.Known.Dots);
                        ctx.SpinnerStyle(Style.Parse("cyan"));

                        var tempSettings = new CentralSyncSettings
                        {
                            ConnectionString = connectionString,
                            ProjectId = projectId,
                            ServerId = serverId,
                            Enabled = true
                        };

                        var service = new CentralSyncService(tempSettings);
                        var statusTask = service.TestConnectionAsync();
                        statusTask.Wait();
                        var status = statusTask.Result;

                        if (!status.IsConnected)
                        {
                            throw new Exception($"Connection failed: {status.ErrorMessage}");
                        }

                        if (!status.IsAuthenticated)
                        {
                            throw new Exception($"Authentication failed: {status.ErrorMessage}");
                        }

                        AnsiConsole.MarkupLine("[green]✅ Connection successful![/]");
                    });

                AnsiConsole.WriteLine();

                // Confirmar guardado
                if (!AnsiConsole.Confirm("💾 Save configuration securely in local SQLite?", true))
                {
                    AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
                    return 0;
                }

                // Guardar configuración
                AnsiConsole.Status()
                    .Start("🔒 Encrypting credentials...", ctx =>
                    {
                        var newSettings = new CentralSyncSettings
                        {
                            Enabled = true,
                            ProjectId = projectId,
                            ServerId = serverId,
                            ConnectionString = connectionString,
                            ApiKey = apiKey,
                            ServerUrl = serverAddress,
                            SyncMode = currentSettings.SyncMode,
                            SyncConfigurations = currentSettings.SyncConfigurations,
                            SyncConnections = currentSettings.SyncConnections,
                            BatchSize = currentSettings.BatchSize
                        };

                        CentralSyncRepository.SaveSettings(newSettings);
                    });

                AnsiConsole.MarkupLine("[green]✅ Configuration saved successfully.[/]");
                AnsiConsole.WriteLine();

                // Información final
                var finalSettings = CentralSyncRepository.GetSettings();

                var summaryPanel = new Panel(new Markup($@"
[bold cyan]📊 Configuration Summary[/]

  • Project ID: [yellow]{finalSettings.ProjectId}[/]
  • Server ID: [yellow]{finalSettings.ServerId}[/]
  • Central Server: [yellow]{serverAddress}[/]
  • Sync Mode: [yellow]{finalSettings.SyncMode}[/]
  • Status: [green]Enabled[/]

[bold]Available commands:[/]
  • [cyan]syncjob central test[/]       - Test connection to central
  • [cyan]syncjob central sync[/]       - Sync executions manually
  • [cyan]syncjob central status[/]     - View configuration status
  • [cyan]syncjob run <config>[/]       - Run sync (auto-sync to central)
"))
                    .Border(BoxBorder.Rounded)
                    .BorderStyle(new Style(Color.Green));

                AnsiConsole.Write(summaryPanel);

                return 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.WriteException(ex);
                return 1;
            }
        }
    }

    // ============================================================================
    // CENTRAL TEST - Probar conexión
    // ============================================================================

    [Description("Test connection to central server")]
    public class CentralTestCommand : AsyncCommand
    {
        public override async Task<int> ExecuteAsync(CommandContext context)
        {
            DbManager.Initialize();

            try
            {
                var settings = CentralSyncRepository.GetSettings();

                if (!CentralSyncRepository.IsConfigured())
                {
                    AnsiConsole.MarkupLine("[red]❌ Central sync is not configured.[/]");
                    AnsiConsole.MarkupLine("[yellow]Run 'syncjob central setup' first.[/]");
                    return 1;
                }

                AnsiConsole.MarkupLine("[bold]Testing connection to central server...[/]");
                AnsiConsole.WriteLine();

                var service = new CentralSyncService(settings);
                CentralConnectionStatus status = null!;

                await AnsiConsole.Status()
                    .StartAsync("🔌 Connecting...", async ctx =>
                    {
                        ctx.Spinner(Spinner.Known.Dots);
                        ctx.SpinnerStyle(Style.Parse("cyan"));

                        status = await service.TestConnectionAsync();
                    });

                // Mostrar resultados
                var table = new Table();
                table.Border(TableBorder.Rounded);
                table.AddColumn(new TableColumn("[bold]Property[/]").Centered());
                table.AddColumn(new TableColumn("[bold]Value[/]"));

                table.AddRow("Configured", status.IsConfigured ? "[green]Yes[/]" : "[red]No[/]");
                table.AddRow("Connected", status.IsConnected ? "[green]Yes[/]" : "[red]No[/]");
                table.AddRow("Authenticated", status.IsAuthenticated ? "[green]Yes[/]" : "[red]No[/]");
                table.AddRow("Project ID", $"[cyan]{status.ProjectId}[/]");
                table.AddRow("Server ID", $"[cyan]{status.ServerId}[/]");
                table.AddRow("Server URL", $"[cyan]{status.ServerUrl}[/]");

                if (status.LastHeartbeat.HasValue)
                {
                    table.AddRow("Last Heartbeat", status.LastHeartbeat.Value.ToString("yyyy-MM-dd HH:mm:ss"));
                }

                if (!string.IsNullOrEmpty(status.ErrorMessage))
                {
                    table.AddRow("Error", $"[red]{status.ErrorMessage}[/]");
                }

                AnsiConsole.Write(table);
                AnsiConsole.WriteLine();

                if (status.IsConnected && status.IsAuthenticated)
                {
                    AnsiConsole.MarkupLine("[green]✅ Connection test successful![/]");
                    return 0;
                }
                else
                {
                    AnsiConsole.MarkupLine("[red]❌ Connection test failed.[/]");
                    return 1;
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.WriteException(ex);
                return 1;
            }
        }
    }

    // ============================================================================
    // CENTRAL STATUS - Ver estado
    // ============================================================================

    [Description("Show central sync status")]
    public class CentralStatusCommand : Command
    {
        public override int Execute(CommandContext context)
        {
            DbManager.Initialize();

            try
            {
                var settings = CentralSyncRepository.GetSettings();
                var isConfigured = CentralSyncRepository.IsConfigured();

                var panel = new Panel(new FigletText("Central Status").Centered().Color(Color.Aqua))
                    .Border(BoxBorder.Double);
                AnsiConsole.Write(panel);
                AnsiConsole.WriteLine();

                if (!isConfigured)
                {
                    AnsiConsole.MarkupLine("[yellow]⚠️  Central sync is not configured.[/]");
                    AnsiConsole.MarkupLine("[dim]Run 'syncjob central setup' to configure.[/]");
                    return 0;
                }

                var grid = new Grid();
                grid.AddColumn();
                grid.AddColumn();

                grid.AddRow("[bold]Project ID:[/]", $"[cyan]{settings.ProjectId}[/]");
                grid.AddRow("[bold]Server ID:[/]", $"[cyan]{settings.ServerId}[/]");
                grid.AddRow("[bold]Central URL:[/]", $"[cyan]{settings.ServerUrl ?? "N/A"}[/]");
                grid.AddRow("[bold]Sync Mode:[/]", $"[yellow]{settings.SyncMode}[/]");
                grid.AddRow("[bold]Status:[/]", settings.Enabled ? "[green]✅ Enabled[/]" : "[red]❌ Disabled[/]");
                grid.AddRow("[bold]Sync Configurations:[/]", settings.SyncConfigurations ? "[green]Yes[/]" : "[dim]No[/]");
                grid.AddRow("[bold]Sync Connections:[/]", settings.SyncConnections ? "[green]Yes[/]" : "[dim]No[/]");

                if (settings.LastSyncAt.HasValue)
                {
                    var timeAgo = DateTime.Now - settings.LastSyncAt.Value;
                    var timeAgoStr = timeAgo.TotalMinutes < 60
                        ? $"{(int)timeAgo.TotalMinutes} minutes ago"
                        : $"{(int)timeAgo.TotalHours} hours ago";

                    grid.AddRow("[bold]Last Sync:[/]", $"{settings.LastSyncAt.Value:yyyy-MM-dd HH:mm:ss} [dim]({timeAgoStr})[/]");
                }
                else
                {
                    grid.AddRow("[bold]Last Sync:[/]", "[dim]Never[/]");
                }

                AnsiConsole.Write(grid);
                AnsiConsole.WriteLine();

                return 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.WriteException(ex);
                return 1;
            }
        }
    }

    // ============================================================================
    // CENTRAL SYNC - Sincronización manual
    // ============================================================================

    [Description("Sync executions to central server")]
    public class CentralSyncCommand : AsyncCommand<CentralSyncCommand.Settings>
    {
        public class Settings : CommandSettings
        {
            [Description("Number of days to sync (default: 7)")]
            [CommandOption("--days")]
            public int Days { get; set; } = 7;

            [Description("Sync all executions")]
            [CommandOption("--all")]
            public bool All { get; set; }
        }

        public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
        {
            DbManager.Initialize();

            try
            {
                var centralSettings = CentralSyncRepository.GetSettings();

                if (!CentralSyncRepository.IsConfigured())
                {
                    AnsiConsole.MarkupLine("[red]❌ Central sync is not configured.[/]");
                    AnsiConsole.MarkupLine("[yellow]Run 'syncjob central setup' first.[/]");
                    return 1;
                }

                if (!centralSettings.Enabled)
                {
                    AnsiConsole.MarkupLine("[yellow]⚠️  Central sync is disabled.[/]");
                    if (AnsiConsole.Confirm("Enable it now?", false))
                    {
                        CentralSyncRepository.SetConfigValue("Enabled", "true");
                        AnsiConsole.MarkupLine("[green]✅ Central sync enabled.[/]");
                    }
                    else
                    {
                        return 0;
                    }
                }

                // Obtener ejecuciones a sincronizar
                var cutoffDate = settings.All ? DateTime.MinValue : DateTime.Now.AddDays(-settings.Days);
                var executions = ExecutionHistoryRepository.GetAll(startDate: cutoffDate, limit: 10000);

                if (executions.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]No executions to sync.[/]");
                    return 0;
                }

                AnsiConsole.MarkupLine($"[bold]Found {executions.Count} execution(s) to sync[/]");
                AnsiConsole.WriteLine();

                // Sincronizar
                var service = new CentralSyncService(centralSettings);
                SyncResult result = null!;

                await AnsiConsole.Status()
                    .StartAsync("📤 Syncing to central...", async ctx =>
                    {
                        ctx.Spinner(Spinner.Known.Dots);
                        ctx.SpinnerStyle(Style.Parse("cyan"));

                        result = await service.SyncExecutionBatchAsync(executions, settings.Days);
                    });

                // Mostrar resultados
                if (result.Success)
                {
                    AnsiConsole.MarkupLine($"[green]✅ Sync completed successfully![/]");
                    AnsiConsole.MarkupLine($"   Processed: [cyan]{result.RecordsProcessed}[/]");
                    AnsiConsole.MarkupLine($"   Duration: [dim]{result.DurationMs} ms[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[yellow]⚠️  Sync completed with errors[/]");
                    AnsiConsole.MarkupLine($"   Processed: [cyan]{result.RecordsProcessed}[/]");
                    AnsiConsole.MarkupLine($"   Failed: [red]{result.RecordsFailed}[/]");

                    if (!string.IsNullOrEmpty(result.ErrorMessage))
                    {
                        AnsiConsole.MarkupLine($"   Error: [red]{result.ErrorMessage}[/]");
                    }
                }

                return result.Success ? 0 : 1;
            }
            catch (Exception ex)
            {
                AnsiConsole.WriteException(ex);
                return 1;
            }
        }
    }

    // ============================================================================
    // CENTRAL ENABLE/DISABLE
    // ============================================================================

    [Description("Enable central synchronization")]
    public class CentralEnableCommand : Command
    {
        public override int Execute(CommandContext context)
        {
            DbManager.Initialize();

            CentralSyncRepository.SetConfigValue("Enabled", "true");
            AnsiConsole.MarkupLine("[green]✅ Central sync enabled.[/]");

            return 0;
        }
    }

    [Description("Disable central synchronization")]
    public class CentralDisableCommand : Command
    {
        public override int Execute(CommandContext context)
        {
            DbManager.Initialize();

            CentralSyncRepository.SetConfigValue("Enabled", "false");
            AnsiConsole.MarkupLine("[yellow]⚠️  Central sync disabled.[/]");
            AnsiConsole.MarkupLine("[dim]Configuration is preserved. Use 'central enable' to re-enable.[/]");

            return 0;
        }
    }

    // ============================================================================
    // CENTRAL RESET - Limpiar configuración
    // ============================================================================

    [Description("Reset central sync configuration")]
    public class CentralResetCommand : Command<CentralResetCommand.Settings>
    {
        public class Settings : CommandSettings
        {
            [Description("Skip confirmation prompt")]
            [CommandOption("--force")]
            public bool Force { get; set; }
        }

        public override int Execute(CommandContext context, Settings settings)
        {
            DbManager.Initialize();

            if (!settings.Force)
            {
                if (!AnsiConsole.Confirm("[red]⚠️  This will delete all central sync configuration. Continue?[/]", false))
                {
                    AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
                    return 0;
                }
            }

            CentralSyncRepository.ClearAllConfig();
            AnsiConsole.MarkupLine("[green]✅ Central sync configuration cleared.[/]");

            return 0;
        }
    }
}
