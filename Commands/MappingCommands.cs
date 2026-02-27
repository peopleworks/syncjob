using Spectre.Console;
using Spectre.Console.Cli;
using SyncJob.Database;
using System;
using System.ComponentModel;
using System.Linq;

namespace SyncJob.Commands
{
    // ============================================================================
    // MAPPING ADD
    // ============================================================================

    public class MappingAddSettings : CommandSettings
    {
        [Description("ID de la configuración")]
        [CommandArgument(0, "<config-id>")]
        public string ConfigId { get; set; } = string.Empty;

        [Description("Columna origen")]
        [CommandOption("--source")]
        public string? SourceColumn { get; set; }

        [Description("Columna destino")]
        [CommandOption("--dest")]
        public string? DestColumn { get; set; }

        [Description("Marcar como Primary Key")]
        [CommandOption("--primary-key")]
        public bool IsPrimaryKey { get; set; }

        [Description("Modo interactivo")]
        [CommandOption("--interactive")]
        public bool Interactive { get; set; }

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(ConfigId))
                return ValidationResult.Error("ConfigId es requerido");
            return ValidationResult.Success();
        }
    }

    public class MappingAddCommand : Command<MappingAddSettings>
    {
        public override int Execute(CommandContext context, MappingAddSettings settings)
        {
            DbManager.Initialize();

            try
            {
                // Verificar que existe la config
                var config = ConfigRepository.GetById(settings.ConfigId);
                if (config == null)
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] Configuración '{settings.ConfigId}' no encontrada.");
                    return 1;
                }

                if (settings.Interactive)
                {
                    AnsiConsole.MarkupLine($"[bold cyan]Agregar Column Mappings a '{settings.ConfigId}'[/]");
                    AnsiConsole.WriteLine();

                    // Mostrar mappings existentes
                    if (config.ColumnMappings.Count > 0)
                    {
                        AnsiConsole.MarkupLine("[bold]Mappings existentes:[/]");
                        var existingTable = new Table();
                        existingTable.Border(TableBorder.Rounded);
                        existingTable.AddColumn("Source");
                        existingTable.AddColumn("Dest");
                        existingTable.AddColumn("PK");

                        foreach (var m in config.ColumnMappings.OrderBy(x => x.Ordinal))
                        {
                            existingTable.AddRow(
                                m.SourceColumn,
                                m.DestColumn,
                                m.IsPrimaryKey ? "[yellow]✓[/]" : "-"
                            );
                        }
                        AnsiConsole.Write(existingTable);
                        AnsiConsole.WriteLine();
                    }

                    // Agregar múltiples mappings
                    bool addMore = true;
                    int ordinal = config.ColumnMappings.Count;

                    while (addMore)
                    {
                        var source = AnsiConsole.Ask<string>("Columna origen:");
                        var dest = AnsiConsole.Ask("Columna destino:", source);
                        var isPK = AnsiConsole.Confirm("¿Es Primary Key?", false);

                        var mapping = new ColumnMappingEntity
                        {
                            ConfigId = settings.ConfigId,
                            SourceColumn = source,
                            DestColumn = dest,
                            IsPrimaryKey = isPK,
                            Ordinal = ordinal++
                        };

                        ColumnMappingRepository.Create(mapping);

                        AnsiConsole.MarkupLine($"[green]✓[/] Mapping agregado: {source} → {dest}");
                        AnsiConsole.WriteLine();

                        addMore = AnsiConsole.Confirm("¿Agregar otro mapping?", true);
                    }

                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[green]✓[/] Mappings agregados correctamente");
                }
                else
                {
                    // Modo no interactivo
                    if (string.IsNullOrWhiteSpace(settings.SourceColumn))
                        throw new Exception("--source es requerido en modo no-interactivo");

                    var dest = settings.DestColumn ?? settings.SourceColumn;

                    var mapping = new ColumnMappingEntity
                    {
                        ConfigId = settings.ConfigId,
                        SourceColumn = settings.SourceColumn,
                        DestColumn = dest,
                        IsPrimaryKey = settings.IsPrimaryKey,
                        Ordinal = config.ColumnMappings.Count
                    };

                    ColumnMappingRepository.Create(mapping);

                    AnsiConsole.MarkupLine($"[green]✓[/] Mapping agregado: {mapping.SourceColumn} → {mapping.DestColumn}");
                }

                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"Ver todos: [cyan]mapping list {settings.ConfigId}[/]");

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
    // MAPPING LIST
    // ============================================================================

    public class MappingListSettings : CommandSettings
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

    public class MappingListCommand : Command<MappingListSettings>
    {
        public override int Execute(CommandContext context, MappingListSettings settings)
        {
            DbManager.Initialize();

            try
            {
                // Verificar que existe la config
                if (!ConfigRepository.Exists(settings.ConfigId))
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] Configuración '{settings.ConfigId}' no encontrada.");
                    return 1;
                }

                var mappings = ColumnMappingRepository.GetByConfigId(settings.ConfigId);

                if (mappings.Count == 0)
                {
                    AnsiConsole.MarkupLine($"[yellow]No hay mappings para '{settings.ConfigId}'.[/]");
                    AnsiConsole.MarkupLine($"Agrega con: [cyan]mapping add {settings.ConfigId} --interactive[/]");
                    return 0;
                }

                var table = new Table();
                table.Border(TableBorder.Rounded);
                table.Title($"[bold]Column Mappings: {settings.ConfigId}[/]");
                table.AddColumn("[bold]#[/]");
                table.AddColumn("[bold]Source Column[/]");
                table.AddColumn("[bold]Dest Column[/]");
                table.AddColumn("[bold]Primary Key[/]");
                table.AddColumn("[bold]Transform[/]");

                foreach (var m in mappings.OrderBy(x => x.Ordinal))
                {
                    table.AddRow(
                        m.MappingId.ToString(),
                        m.SourceColumn,
                        m.DestColumn,
                        m.IsPrimaryKey ? "[yellow]✓ PK[/]" : "-",
                        m.TransformType ?? "-"
                    );
                }

                AnsiConsole.Write(table);
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"Total: [bold]{mappings.Count}[/] mapping(s)");

                // Contar PKs
                var pkCount = mappings.Count(m => m.IsPrimaryKey);
                if (pkCount == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]Advertencia:[/] No hay Primary Keys definidos.");
                    AnsiConsole.MarkupLine("Necesitas PKs para estrategias Upsert/Full.");
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
    // MAPPING REMOVE
    // ============================================================================

    public class MappingRemoveSettings : CommandSettings
    {
        [Description("ID de la configuración")]
        [CommandArgument(0, "<config-id>")]
        public string ConfigId { get; set; } = string.Empty;

        [Description("ID del mapping a eliminar")]
        [CommandOption("--mapping-id")]
        public int? MappingId { get; set; }

        [Description("Columna a eliminar")]
        [CommandOption("--column")]
        public string? ColumnName { get; set; }

        [Description("Eliminar todos los mappings")]
        [CommandOption("--all")]
        public bool All { get; set; }

        [Description("No pedir confirmación")]
        [CommandOption("--force")]
        public bool Force { get; set; }

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(ConfigId))
                return ValidationResult.Error("ConfigId es requerido");

            if (!MappingId.HasValue && string.IsNullOrWhiteSpace(ColumnName) && !All)
                return ValidationResult.Error("Debes especificar --mapping-id, --column o --all");

            return ValidationResult.Success();
        }
    }

    public class MappingRemoveCommand : Command<MappingRemoveSettings>
    {
        public override int Execute(CommandContext context, MappingRemoveSettings settings)
        {
            DbManager.Initialize();

            try
            {
                if (!ConfigRepository.Exists(settings.ConfigId))
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] Configuración '{settings.ConfigId}' no encontrada.");
                    return 1;
                }

                if (settings.All)
                {
                    if (!settings.Force)
                    {
                        var confirm = AnsiConsole.Confirm(
                            $"¿Eliminar TODOS los mappings de '{settings.ConfigId}'?", false);
                        if (!confirm)
                        {
                            AnsiConsole.MarkupLine("[yellow]Cancelado.[/]");
                            return 0;
                        }
                    }

                    ColumnMappingRepository.DeleteByConfigId(settings.ConfigId);
                    AnsiConsole.MarkupLine($"[green]✓[/] Todos los mappings eliminados.");
                    return 0;
                }

                if (settings.MappingId.HasValue)
                {
                    if (!settings.Force)
                    {
                        var confirm = AnsiConsole.Confirm(
                            $"¿Eliminar mapping #{settings.MappingId}?", false);
                        if (!confirm)
                        {
                            AnsiConsole.MarkupLine("[yellow]Cancelado.[/]");
                            return 0;
                        }
                    }

                    ColumnMappingRepository.Delete(settings.MappingId.Value);
                    AnsiConsole.MarkupLine($"[green]✓[/] Mapping #{settings.MappingId} eliminado.");
                    return 0;
                }

                if (!string.IsNullOrWhiteSpace(settings.ColumnName))
                {
                    var mappings = ColumnMappingRepository.GetByConfigId(settings.ConfigId);
                    var mapping = mappings.FirstOrDefault(m =>
                        m.SourceColumn.Equals(settings.ColumnName, StringComparison.OrdinalIgnoreCase) ||
                        m.DestColumn.Equals(settings.ColumnName, StringComparison.OrdinalIgnoreCase));

                    if (mapping == null)
                    {
                        AnsiConsole.MarkupLine($"[red]Error:[/] No se encontró mapping para columna '{settings.ColumnName}'.");
                        return 1;
                    }

                    if (!settings.Force)
                    {
                        var confirm = AnsiConsole.Confirm(
                            $"¿Eliminar mapping {mapping.SourceColumn} → {mapping.DestColumn}?", false);
                        if (!confirm)
                        {
                            AnsiConsole.MarkupLine("[yellow]Cancelado.[/]");
                            return 0;
                        }
                    }

                    ColumnMappingRepository.Delete(mapping.MappingId);
                    AnsiConsole.MarkupLine($"[green]✓[/] Mapping {mapping.SourceColumn} → {mapping.DestColumn} eliminado.");
                    return 0;
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
    // MAPPING CLEAR (alias para remove --all)
    // ============================================================================

    public class MappingClearSettings : CommandSettings
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

    public class MappingClearCommand : Command<MappingClearSettings>
    {
        public override int Execute(CommandContext context, MappingClearSettings settings)
        {
            DbManager.Initialize();

            try
            {
                if (!ConfigRepository.Exists(settings.ConfigId))
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] Configuración '{settings.ConfigId}' no encontrada.");
                    return 1;
                }

                var mappings = ColumnMappingRepository.GetByConfigId(settings.ConfigId);
                if (mappings.Count == 0)
                {
                    AnsiConsole.MarkupLine($"[yellow]No hay mappings para eliminar.[/]");
                    return 0;
                }

                if (!settings.Force)
                {
                    AnsiConsole.MarkupLine($"[yellow]Se eliminarán {mappings.Count} mapping(s):[/]");
                    foreach (var m in mappings.Take(5))
                    {
                        AnsiConsole.MarkupLine($"  - {m.SourceColumn} → {m.DestColumn}");
                    }
                    if (mappings.Count > 5)
                        AnsiConsole.MarkupLine($"  ... y {mappings.Count - 5} más");

                    AnsiConsole.WriteLine();
                    var confirm = AnsiConsole.Confirm("¿Continuar?", false);
                    if (!confirm)
                    {
                        AnsiConsole.MarkupLine("[yellow]Cancelado.[/]");
                        return 0;
                    }
                }

                ColumnMappingRepository.DeleteByConfigId(settings.ConfigId);
                AnsiConsole.MarkupLine($"[green]✓[/] {mappings.Count} mapping(s) eliminado(s).");
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
