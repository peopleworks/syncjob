using Spectre.Console;
using Spectre.Console.Cli;
using SyncJob.Database;
using System;
using System.ComponentModel;
using System.IO;

namespace SyncJob.Commands
{
    // ============================================================================
    // DB INFO
    // ============================================================================

    public class DbInfoSettings : CommandSettings
    {
    }

    public class DbInfoCommand : Command<DbInfoSettings>
    {
        public override int Execute(CommandContext context, DbInfoSettings settings)
        {
            DbManager.Initialize();

            try
            {
                var info = DbManager.GetInfo();

                var panel = new Panel("[bold cyan]SyncJob Database Information[/]")
                    .Border(BoxBorder.Double)
                    .BorderStyle(new Style(Color.Aqua));
                AnsiConsole.Write(panel);
                AnsiConsole.WriteLine();

                var grid = new Grid();
                grid.AddColumn();
                grid.AddColumn();

                grid.AddRow("[bold]Path:[/]", info.Path);
                grid.AddRow("[bold]Size:[/]", $"{info.SizeMB} MB");
                grid.AddRow("[bold]Schema Version:[/]", info.SchemaVersion);
                grid.AddRow("[bold]Configurations:[/]", info.ConfigCount.ToString());
                grid.AddRow("[bold]Connections:[/]", info.ConnectionCount.ToString());
                grid.AddRow("[bold]Executions:[/]", info.ExecutionCount.ToString());

                AnsiConsole.Write(grid);
                AnsiConsole.WriteLine();

                // Tips
                AnsiConsole.MarkupLine("[bold]Comandos útiles:[/]");
                AnsiConsole.MarkupLine("  [cyan]db backup[/] --output backup.db     Hacer backup");
                AnsiConsole.MarkupLine("  [cyan]db cleanup[/] --older-than 90      Limpiar ejecuciones > 90 días");
                AnsiConsole.MarkupLine("  [cyan]db vacuum[/]                       Compactar base de datos");

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
    // DB BACKUP
    // ============================================================================

    public class DbBackupSettings : CommandSettings
    {
        [Description("Ruta del archivo de backup")]
        [CommandOption("--output")]
        public string? OutputPath { get; set; }
    }

    public class DbBackupCommand : Command<DbBackupSettings>
    {
        public override int Execute(CommandContext context, DbBackupSettings settings)
        {
            DbManager.Initialize();

            try
            {
                var output = settings.OutputPath;
                if (string.IsNullOrWhiteSpace(output))
                {
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    output = $"SyncJob_backup_{timestamp}.db";
                }

                AnsiConsole.Status()
                    .Start("Creando backup...", ctx =>
                    {
                        DbManager.Backup(output);
                    });

                var fileInfo = new FileInfo(output);

                AnsiConsole.MarkupLine($"[green]✓[/] Backup creado correctamente");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[bold]Archivo:[/] {output}");
                AnsiConsole.MarkupLine($"[bold]Tamaño:[/] {fileInfo.Length / 1024.0 / 1024.0:F2} MB");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("Para restaurar: [cyan]db restore --file " + output + "[/]");

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
    // DB RESTORE
    // ============================================================================

    public class DbRestoreSettings : CommandSettings
    {
        [Description("Ruta del archivo de backup")]
        [CommandOption("--file")]
        public string? FilePath { get; set; }

        [Description("No pedir confirmación")]
        [CommandOption("--force")]
        public bool Force { get; set; }

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(FilePath))
                return ValidationResult.Error("--file es requerido");
            return ValidationResult.Success();
        }
    }

    public class DbRestoreCommand : Command<DbRestoreSettings>
    {
        public override int Execute(CommandContext context, DbRestoreSettings settings)
        {
            try
            {
                if (!File.Exists(settings.FilePath))
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] Archivo no encontrado: {settings.FilePath}");
                    return 1;
                }

                if (!settings.Force)
                {
                    AnsiConsole.MarkupLine("[yellow]ADVERTENCIA:[/] Esta acción sobrescribirá la base de datos actual.");
                    var confirm = AnsiConsole.Confirm("¿Continuar?", false);
                    if (!confirm)
                    {
                        AnsiConsole.MarkupLine("[yellow]Cancelado.[/]");
                        return 0;
                    }
                }

                AnsiConsole.Status()
                    .Start("Restaurando backup...", ctx =>
                    {
                        DbManager.Restore(settings.FilePath!);
                    });

                AnsiConsole.MarkupLine("[green]✓[/] Backup restaurado correctamente");
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
    // DB CLEANUP
    // ============================================================================

    public class DbCleanupSettings : CommandSettings
    {
        [Description("Eliminar ejecuciones más antiguas que N días")]
        [CommandOption("--older-than")]
        public int OlderThanDays { get; set; } = 90;

        [Description("No pedir confirmación")]
        [CommandOption("--force")]
        public bool Force { get; set; }
    }

    public class DbCleanupCommand : Command<DbCleanupSettings>
    {
        public override int Execute(CommandContext context, DbCleanupSettings settings)
        {
            DbManager.Initialize();

            try
            {
                if (!settings.Force)
                {
                    var confirm = AnsiConsole.Confirm(
                        $"¿Eliminar ejecuciones más antiguas que {settings.OlderThanDays} días?", false);
                    if (!confirm)
                    {
                        AnsiConsole.MarkupLine("[yellow]Cancelado.[/]");
                        return 0;
                    }
                }

                int deleted = 0;
                AnsiConsole.Status()
                    .Start("Limpiando registros antiguos...", ctx =>
                    {
                        deleted = DbManager.CleanupOldExecutions(settings.OlderThanDays);
                    });

                AnsiConsole.MarkupLine($"[green]✓[/] {deleted} registro(s) eliminado(s)");
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
    // DB VACUUM
    // ============================================================================

    public class DbVacuumSettings : CommandSettings
    {
    }

    public class DbVacuumCommand : Command<DbVacuumSettings>
    {
        public override int Execute(CommandContext context, DbVacuumSettings settings)
        {
            DbManager.Initialize();

            try
            {
                var infoBefore = DbManager.GetInfo();

                AnsiConsole.Status()
                    .Start("Compactando base de datos...", ctx =>
                    {
                        DbManager.Vacuum();
                    });

                var infoAfter = DbManager.GetInfo();
                var savedMB = double.Parse(infoBefore.SizeMB) - double.Parse(infoAfter.SizeMB);

                AnsiConsole.MarkupLine("[green]✓[/] Base de datos compactada");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[bold]Tamaño anterior:[/] {infoBefore.SizeMB} MB");
                AnsiConsole.MarkupLine($"[bold]Tamaño actual:[/] {infoAfter.SizeMB} MB");
                if (savedMB > 0)
                    AnsiConsole.MarkupLine($"[bold]Espacio liberado:[/] [green]{savedMB:F2} MB[/]");

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
