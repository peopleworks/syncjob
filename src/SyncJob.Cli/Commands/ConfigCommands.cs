using Spectre.Console;
using Spectre.Console.Cli;
using SyncJob.Database;
using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;

namespace SyncJob.Commands
{
    // ============================================================================
    // CONFIG CREATE
    // ============================================================================

    public class ConfigCreateSettings : CommandSettings
    {
        [Description("Nombre único de la configuración (ID)")]
        [CommandArgument(0, "<config-id>")]
        public string ConfigId { get; set; } = string.Empty;

        [Description("Nombre descriptivo para mostrar")]
        [CommandOption("--display-name")]
        public string? DisplayName { get; set; }

        [Description("ID de conexión origen")]
        [CommandOption("--source-conn")]
        public string? SourceConnectionId { get; set; }

        [Description("Query SQL del origen")]
        [CommandOption("--source-query")]
        public string? SourceQuery { get; set; }

        [Description("ID de conexión destino")]
        [CommandOption("--dest-conn")]
        public string? DestConnectionId { get; set; }

        [Description("Tabla Stage en destino")]
        [CommandOption("--dest-stage")]
        public string? DestStageTable { get; set; }

        [Description("Tabla Final en destino")]
        [CommandOption("--dest-final")]
        public string? DestFinalTable { get; set; }

        [Description("Modo de tracking (Snapshot, Timestamp, RowVersion)")]
        [CommandOption("--tracking-mode")]
        public string? TrackingMode { get; set; }

        [Description("Columna de tracking para Timestamp/RowVersion")]
        [CommandOption("--tracking-column")]
        public string? TrackingColumn { get; set; }

        [Description("Estrategia de merge (Insert, Upsert, Full)")]
        [CommandOption("--merge-strategy")]
        public string? MergeStrategy { get; set; }

        [Description("Modo interactivo (pregunta valores)")]
        [CommandOption("--interactive")]
        public bool Interactive { get; set; }

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(ConfigId))
                return ValidationResult.Error("ConfigId es requerido");
            return ValidationResult.Success();
        }
    }

    public class ConfigCreateCommand : Command<ConfigCreateSettings>
    {
        public override int Execute(CommandContext context, ConfigCreateSettings settings)
        {
            DbManager.Initialize();

            try
            {
                // Verificar si ya existe
                if (ConfigRepository.Exists(settings.ConfigId))
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] La configuración '{settings.ConfigId}' ya existe.");
                    AnsiConsole.MarkupLine("Use [yellow]config delete[/] primero o elija otro ID.");
                    return 1;
                }

                var config = new ConfigurationEntity
                {
                    ConfigId = settings.ConfigId,
                    DisplayName = settings.DisplayName ?? settings.ConfigId,
                    CreatedBy = Environment.UserName
                };

                if (settings.Interactive)
                {
                    // Modo interactivo
                    AnsiConsole.MarkupLine("[bold cyan]Creación Interactiva de Configuración[/]");
                    AnsiConsole.WriteLine();

                    config.DisplayName = AnsiConsole.Ask<string>("Nombre descriptivo:", config.DisplayName);
                    config.Description = AnsiConsole.Ask<string>("Descripción (opcional):", string.Empty);

                    // Listar conexiones disponibles
                    var connections = ConnectionRepository.ListAll(activeOnly: true);
                    if (connections.Count == 0)
                    {
                        AnsiConsole.MarkupLine("[yellow]No hay conexiones disponibles.[/]");
                        AnsiConsole.MarkupLine("Crea una con: [cyan]connection add[/]");
                        return 1;
                    }

                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[bold]Conexiones disponibles:[/]");
                    var connTable = new Table();
                    connTable.AddColumn("ID");
                    connTable.AddColumn("Nombre");
                    connTable.AddColumn("Servidor");
                    connTable.AddColumn("Base de Datos");
                    foreach (var c in connections)
                    {
                        connTable.AddRow(c.ConnectionId, c.DisplayName, c.ServerName, c.DatabaseName);
                    }
                    AnsiConsole.Write(connTable);
                    AnsiConsole.WriteLine();

                    config.SourceConnectionId = AnsiConsole.Ask<string>("ID de conexión origen:");
                    config.SourceQuery = AnsiConsole.Ask<string>("Query SQL origen:");
                    config.DestConnectionId = AnsiConsole.Ask<string>("ID de conexión destino:");
                    config.DestStageTable = AnsiConsole.Ask<string>("Tabla Stage (opcional):", string.Empty);
                    config.DestFinalTable = AnsiConsole.Ask<string>("Tabla Final:");

                    var trackingMode = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("Modo de tracking:")
                            .AddChoices("Snapshot", "Timestamp", "RowVersion", "None"));
                    config.TrackingMode = trackingMode;

                    if (trackingMode == "Timestamp" || trackingMode == "RowVersion")
                    {
                        config.TrackingColumn = AnsiConsole.Ask<string>("Columna de tracking:");
                    }

                    var mergeStrategy = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("Estrategia de merge:")
                            .AddChoices("Insert", "Upsert", "Full"));
                    config.MergeStrategy = mergeStrategy;

                    config.BatchSize = AnsiConsole.Ask("BatchSize:", 10000);
                    config.MaxDOP = AnsiConsole.Ask("MaxDOP:", 4);
                }
                else
                {
                    // Modo no interactivo (usar parámetros)
                    config.SourceConnectionId = settings.SourceConnectionId ?? throw new Exception("--source-conn es requerido");
                    config.SourceQuery = settings.SourceQuery ?? throw new Exception("--source-query es requerido");
                    config.DestConnectionId = settings.DestConnectionId ?? throw new Exception("--dest-conn es requerido");
                    config.DestFinalTable = settings.DestFinalTable ?? throw new Exception("--dest-final es requerido");
                    config.DestStageTable = settings.DestStageTable;
                    config.TrackingMode = settings.TrackingMode ?? "Snapshot";
                    config.TrackingColumn = settings.TrackingColumn;
                    config.MergeStrategy = settings.MergeStrategy ?? "Upsert";
                }

                // Validar conexiones existen
                if (!ConnectionRepository.Exists(config.SourceConnectionId))
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] Conexión origen '{config.SourceConnectionId}' no existe.");
                    return 1;
                }
                if (!ConnectionRepository.Exists(config.DestConnectionId))
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] Conexión destino '{config.DestConnectionId}' no existe.");
                    return 1;
                }

                // Crear configuración
                ConfigRepository.Create(config);

                AnsiConsole.WriteLine();
                var panel = new Panel($"[green]✓[/] Configuración '[bold]{config.ConfigId}[/]' creada correctamente")
                    .Border(BoxBorder.Rounded)
                    .BorderStyle(new Style(Color.Green));
                AnsiConsole.Write(panel);

                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("Próximos pasos:");
                AnsiConsole.MarkupLine($"1. Agregar mappings: [cyan]mapping add {config.ConfigId} --interactive[/]");
                AnsiConsole.MarkupLine($"2. Validar: [cyan]validate --config-id {config.ConfigId}[/]");
                AnsiConsole.MarkupLine($"3. Ejecutar: [cyan]run {config.ConfigId}[/]");

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
    // CONFIG LIST
    // ============================================================================

    public class ConfigListSettings : CommandSettings
    {
        [Description("Mostrar solo activos")]
        [CommandOption("--active-only")]
        public bool ActiveOnly { get; set; }

        [Description("Mostrar solo inactivos")]
        [CommandOption("--inactive-only")]
        public bool InactiveOnly { get; set; }

        [Description("Formato de salida (table, json, csv)")]
        [CommandOption("--format")]
        public string Format { get; set; } = "table";
    }

    public class ConfigListCommand : Command<ConfigListSettings>
    {
        public override int Execute(CommandContext context, ConfigListSettings settings)
        {
            DbManager.Initialize();

            try
            {
                bool? activeFilter = null;
                if (settings.ActiveOnly) activeFilter = true;
                if (settings.InactiveOnly) activeFilter = false;

                var configs = ConfigRepository.ListAll(activeFilter);

                if (configs.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]No hay configuraciones.[/]");
                    AnsiConsole.MarkupLine("Crea una con: [cyan]config create <config-id> --interactive[/]");
                    return 0;
                }

                if (settings.Format == "json")
                {
                    var json = JsonSerializer.Serialize(configs, new JsonSerializerOptions { WriteIndented = true });
                    Console.WriteLine(json);
                    return 0;
                }

                if (settings.Format == "csv")
                {
                    Console.WriteLine("ConfigId,DisplayName,IsActive,TrackingMode,MappingsCount,LastExecution,LastStatus");
                    foreach (var c in configs)
                    {
                        Console.WriteLine($"{c.ConfigId},{c.DisplayName},{c.IsActive},{c.TrackingMode},{c.MappingsCount},{c.LastExecution},{c.LastExecutionStatus}");
                    }
                    return 0;
                }

                // Table format (default)
                var table = new Table();
                table.Border(TableBorder.Rounded);
                table.AddColumn("[bold]ConfigId[/]");
                table.AddColumn("[bold]Nombre[/]");
                table.AddColumn("[bold]Estado[/]");
                table.AddColumn("[bold]Tracking[/]");
                table.AddColumn("[bold]Mappings[/]");
                table.AddColumn("[bold]Última Ejecución[/]");

                foreach (var c in configs)
                {
                    var status = c.IsActive ? "[green]✓ Activo[/]" : "[grey]✗ Inactivo[/]";
                    var lastExec = c.LastExecution.HasValue
                        ? $"{GetRelativeTime(c.LastExecution.Value)} ({GetStatusIcon(c.LastExecutionStatus)})"
                        : "[grey]Nunca[/]";

                    table.AddRow(
                        c.ConfigId,
                        c.DisplayName ?? "-",
                        status,
                        c.TrackingMode,
                        c.MappingsCount.ToString(),
                        lastExec
                    );
                }

                AnsiConsole.Write(table);
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"Total: [bold]{configs.Count}[/] configuración(es)");

                return 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.WriteException(ex);
                return 1;
            }
        }

        private static string GetRelativeTime(DateTime dt)
        {
            var diff = DateTime.UtcNow - dt;
            if (diff.TotalMinutes < 1) return "Hace instantes";
            if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
            if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
            if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";
            return dt.ToString("yyyy-MM-dd");
        }

        private static string GetStatusIcon(string? status)
        {
            return status switch
            {
                "Success" => "[green]OK[/]",
                "Failed" => "[red]FAIL[/]",
                "Running" => "[yellow]RUN[/]",
                _ => "[grey]-[/]"
            };
        }
    }

    // ============================================================================
    // CONFIG SHOW
    // ============================================================================

    public class ConfigShowSettings : CommandSettings
    {
        [Description("ID de la configuración")]
        [CommandArgument(0, "<config-id>")]
        public string ConfigId { get; set; } = string.Empty;

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(ConfigId))
                return ValidationResult.Error("ConfigId es requerido");
            return ValidationResult.Success();
        }
    }

    public class ConfigShowCommand : Command<ConfigShowSettings>
    {
        public override int Execute(CommandContext context, ConfigShowSettings settings)
        {
            DbManager.Initialize();

            try
            {
                var config = ConfigRepository.GetById(settings.ConfigId);
                if (config == null)
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] Configuración '{settings.ConfigId}' no encontrada.");
                    return 1;
                }

                // Panel principal
                var panel = new Panel($"[bold cyan]{config.DisplayName}[/]\n[grey]{config.Description ?? "Sin descripción"}[/]")
                    .Header($"[yellow]⚙ {config.ConfigId}[/]")
                    .Border(BoxBorder.Double)
                    .BorderStyle(new Style(Color.Aqua));
                AnsiConsole.Write(panel);
                AnsiConsole.WriteLine();

                // Detalles
                var grid = new Grid();
                grid.AddColumn();
                grid.AddColumn();

                grid.AddRow("[bold]Estado:[/]", config.IsActive ? "[green]✓ Activo[/]" : "[grey]✗ Inactivo[/]");
                grid.AddRow("[bold]Tracking Mode:[/]", config.TrackingMode);
                grid.AddRow("[bold]Tracking Column:[/]", config.TrackingColumn ?? "-");
                grid.AddRow("[bold]Merge Strategy:[/]", config.MergeStrategy);
                grid.AddRow("[bold]BatchSize:[/]", config.BatchSize.ToString());
                grid.AddRow("[bold]MaxDOP:[/]", config.MaxDOP.ToString());
                grid.AddRow("[bold]Conexión Origen:[/]", config.SourceConnectionId);
                grid.AddRow("[bold]Conexión Destino:[/]", config.DestConnectionId);
                grid.AddRow("[bold]Tabla Stage:[/]", config.DestStageTable ?? "-");
                grid.AddRow("[bold]Tabla Final:[/]", config.DestFinalTable);
                grid.AddRow("[bold]Creado:[/]", config.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"));
                grid.AddRow("[bold]Actualizado:[/]", config.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss"));

                AnsiConsole.Write(grid);
                AnsiConsole.WriteLine();

                // Mappings
                if (config.ColumnMappings.Count > 0)
                {
                    AnsiConsole.MarkupLine("[bold]Column Mappings:[/]");
                    var mappingTable = new Table();
                    mappingTable.Border(TableBorder.Rounded);
                    mappingTable.AddColumn("Source");
                    mappingTable.AddColumn("Dest");
                    mappingTable.AddColumn("PK");

                    foreach (var m in config.ColumnMappings.OrderBy(x => x.Ordinal))
                    {
                        mappingTable.AddRow(
                            m.SourceColumn,
                            m.DestColumn,
                            m.IsPrimaryKey ? "[yellow]✓[/]" : "-"
                        );
                    }

                    AnsiConsole.Write(mappingTable);
                    AnsiConsole.WriteLine();
                }
                else
                {
                    AnsiConsole.MarkupLine("[yellow]Sin column mappings.[/]");
                    AnsiConsole.MarkupLine($"Agrega con: [cyan]mapping add {config.ConfigId} --interactive[/]");
                    AnsiConsole.WriteLine();
                }

                // Query
                if (!string.IsNullOrWhiteSpace(config.SourceQuery))
                {
                    AnsiConsole.MarkupLine("[bold]Source Query:[/]");
                    var queryPanel = new Panel(config.SourceQuery)
                        .Border(BoxBorder.Rounded)
                        .BorderStyle(new Style(Color.Grey));
                    AnsiConsole.Write(queryPanel);
                }

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
    // CONFIG DELETE
    // ============================================================================

    public class ConfigDeleteSettings : CommandSettings
    {
        [Description("ID de la configuración")]
        [CommandArgument(0, "<config-id>")]
        public string ConfigId { get; set; } = string.Empty;

        [Description("No pedir confirmación")]
        [CommandOption("--force")]
        public bool Force { get; set; }

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(ConfigId))
                return ValidationResult.Error("ConfigId es requerido");
            return ValidationResult.Success();
        }
    }

    public class ConfigDeleteCommand : Command<ConfigDeleteSettings>
    {
        public override int Execute(CommandContext context, ConfigDeleteSettings settings)
        {
            DbManager.Initialize();

            try
            {
                if (!ConfigRepository.Exists(settings.ConfigId))
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] Configuración '{settings.ConfigId}' no encontrada.");
                    return 1;
                }

                if (!settings.Force)
                {
                    var confirm = AnsiConsole.Confirm($"¿Eliminar configuración '{settings.ConfigId}'?", false);
                    if (!confirm)
                    {
                        AnsiConsole.MarkupLine("[yellow]Cancelado.[/]");
                        return 0;
                    }
                }

                ConfigRepository.Delete(settings.ConfigId);

                AnsiConsole.MarkupLine($"[green]✓[/] Configuración '{settings.ConfigId}' eliminada correctamente.");
                return 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.WriteException(ex);
                return 1;
            }
        }
    }
}
