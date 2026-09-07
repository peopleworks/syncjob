using Spectre.Console;
using Spectre.Console.Cli;
using SyncJob.Database;
using System;
using System.ComponentModel;
using System.Linq;

namespace SyncJob.Commands
{
    // ============================================================================
    // HISTORY LIST
    // ============================================================================

    public class HistoryListSettings : CommandSettings
    {
        [Description("ID de configuración (opcional)")]
        [CommandArgument(0, "[config-id]")]
        public string? ConfigId { get; set; }

        [Description("Filtrar por estado (Success, Failed, Running)")]
        [CommandOption("--status")]
        public string? Status { get; set; }

        [Description("Número de registros a mostrar")]
        [CommandOption("--limit")]
        public int Limit { get; set; } = 50;

        [Description("Mostrar últimos N días")]
        [CommandOption("--days")]
        public int? Days { get; set; }

        [Description("Formato de salida (table, json)")]
        [CommandOption("--format")]
        public string Format { get; set; } = "table";
    }

    public class HistoryListCommand : Command<HistoryListSettings>
    {
        public override int Execute(CommandContext context, HistoryListSettings settings)
        {
            DbManager.Initialize();

            try
            {
                // Calcular fechas
                DateTime? startDate = null;
                if (settings.Days.HasValue)
                {
                    startDate = DateTime.Now.AddDays(-settings.Days.Value);
                }

                // Obtener historial
                var executions = string.IsNullOrWhiteSpace(settings.ConfigId)
                    ? ExecutionHistoryRepository.GetAll(settings.Status, startDate, null, settings.Limit)
                    : ExecutionHistoryRepository.GetByConfigId(settings.ConfigId, settings.Limit);

                if (settings.Status != null && !string.IsNullOrWhiteSpace(settings.ConfigId))
                {
                    // Filtrar por status si se especificó configId
                    executions = executions.Where(e => e.Status.Equals(settings.Status, StringComparison.OrdinalIgnoreCase)).ToList();
                }

                if (executions.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]No hay ejecuciones registradas.[/]");
                    return 0;
                }

                if (settings.Format.Equals("json", StringComparison.OrdinalIgnoreCase))
                {
                    // Formato JSON
                    var json = System.Text.Json.JsonSerializer.Serialize(executions, new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = true
                    });
                    Console.WriteLine(json);
                    return 0;
                }

                // Formato Tabla
                var table = new Table();
                table.Border(TableBorder.Rounded);
                table.Title(string.IsNullOrWhiteSpace(settings.ConfigId)
                    ? "[bold]Historial de Ejecuciones[/]"
                    : $"[bold]Historial: {settings.ConfigId}[/]");

                table.AddColumn("[bold]ID[/]");
                table.AddColumn("[bold]Config[/]");
                table.AddColumn("[bold]Inicio[/]");
                table.AddColumn("[bold]Duración[/]");
                table.AddColumn("[bold]Estado[/]");
                table.AddColumn("[bold]Leídos[/]");
                table.AddColumn("[bold]Insertados[/]");
                table.AddColumn("[bold]Actualizados[/]");
                table.AddColumn("[bold]Eliminados[/]");

                foreach (var exec in executions)
                {
                    var statusMarkup = exec.Status switch
                    {
                        "Success" => "[green]Success[/]",
                        "Failed" => "[red]Failed[/]",
                        "Running" => "[yellow]Running[/]",
                        "Cancelled" => "[gray]Cancelled[/]",
                        _ => exec.Status
                    };

                    var duration = exec.DurationMs.HasValue
                        ? FormatDuration(exec.DurationMs.Value)
                        : "-";

                    table.AddRow(
                        exec.ExecutionId.Substring(0, 8) + "...",
                        exec.ConfigId,
                        exec.StartTime.ToString("yyyy-MM-dd HH:mm"),
                        duration,
                        statusMarkup,
                        exec.RowsRead.ToString("N0"),
                        exec.RowsInserted.ToString("N0"),
                        exec.RowsUpdated.ToString("N0"),
                        exec.RowsDeleted.ToString("N0")
                    );
                }

                AnsiConsole.Write(table);
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"Total: [bold]{executions.Count}[/] ejecución(es)");

                // Mostrar hint
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[dim]Ver detalles: history show <execution-id>[/]");

                return 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.WriteException(ex);
                return 1;
            }
        }

        private string FormatDuration(long ms)
        {
            var ts = TimeSpan.FromMilliseconds(ms);
            if (ts.TotalHours >= 1)
                return $"{ts.Hours}h {ts.Minutes}m";
            if (ts.TotalMinutes >= 1)
                return $"{ts.Minutes}m {ts.Seconds}s";
            if (ts.TotalSeconds >= 1)
                return $"{ts.Seconds}s";
            return $"{ms}ms";
        }
    }

    // ============================================================================
    // HISTORY SHOW
    // ============================================================================

    public class HistoryShowSettings : CommandSettings
    {
        [Description("ID de ejecución")]
        [CommandArgument(0, "<execution-id>")]
        public string ExecutionId { get; set; } = string.Empty;

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(ExecutionId))
                return ValidationResult.Error("ExecutionId es requerido");
            return ValidationResult.Success();
        }
    }

    public class HistoryShowCommand : Command<HistoryShowSettings>
    {
        public override int Execute(CommandContext context, HistoryShowSettings settings)
        {
            DbManager.Initialize();

            try
            {
                var execution = ExecutionHistoryRepository.GetById(settings.ExecutionId);
                if (execution == null)
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] Ejecución '{settings.ExecutionId}' no encontrada.");
                    return 1;
                }

                var panel = new Panel($"[bold cyan]Detalles de Ejecución: {settings.ExecutionId}[/]")
                    .Border(BoxBorder.Double)
                    .BorderStyle(new Style(Color.Aqua));
                AnsiConsole.Write(panel);
                AnsiConsole.WriteLine();

                // Información General
                var grid = new Grid();
                grid.AddColumn();
                grid.AddColumn();

                grid.AddRow("[bold]Configuración:[/]", execution.ConfigId);
                grid.AddRow("[bold]Estado:[/]", GetStatusMarkup(execution.Status));
                grid.AddRow("[bold]Modo de Ejecución:[/]", execution.ExecutionMode ?? "N/A");
                grid.AddRow("");
                grid.AddRow("[bold cyan]Tiempos[/]", "");
                grid.AddRow("  Inicio:", execution.StartTime.ToString("yyyy-MM-dd HH:mm:ss"));
                grid.AddRow("  Fin:", execution.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "En ejecución");
                if (execution.DurationMs.HasValue)
                {
                    var ts = TimeSpan.FromMilliseconds(execution.DurationMs.Value);
                    grid.AddRow("  Duración:", $"{ts.Hours}h {ts.Minutes}m {ts.Seconds}s ({execution.DurationMs.Value:N0} ms)");
                }

                grid.AddRow("");
                grid.AddRow("[bold cyan]Estadísticas de Filas[/]", "");
                grid.AddRow("  Leídas:", execution.RowsRead.ToString("N0"));
                grid.AddRow("  Insertadas:", execution.RowsInserted.ToString("N0"));
                grid.AddRow("  Actualizadas:", execution.RowsUpdated.ToString("N0"));
                grid.AddRow("  Eliminadas:", execution.RowsDeleted.ToString("N0"));
                grid.AddRow("  Omitidas:", execution.RowsSkipped.ToString("N0"));
                grid.AddRow("  Fallidas:", execution.RowsFailed.ToString("N0"));

                var totalProcessed = execution.RowsInserted + execution.RowsUpdated + execution.RowsDeleted;
                grid.AddRow("  [bold]Total Procesadas:[/]", $"[bold]{totalProcessed:N0}[/]");

                if (!string.IsNullOrWhiteSpace(execution.HostMachine))
                {
                    grid.AddRow("");
                    grid.AddRow("[bold cyan]Información de Sistema[/]", "");
                    grid.AddRow("  Máquina:", execution.HostMachine);
                }

                if (!string.IsNullOrWhiteSpace(execution.TriggeredBy))
                {
                    grid.AddRow("  Disparado por:", execution.TriggeredBy);
                }

                if (!string.IsNullOrWhiteSpace(execution.LogFilePath))
                {
                    grid.AddRow("  Log File:", execution.LogFilePath);
                }

                AnsiConsole.Write(grid);
                AnsiConsole.WriteLine();

                // Mostrar error si existe
                if (!string.IsNullOrWhiteSpace(execution.ErrorMessage))
                {
                    AnsiConsole.WriteLine();
                    var errorPanel = new Panel(new Markup($"[red]{execution.ErrorMessage.EscapeMarkup()}[/]"))
                        .Header("[bold red]Error[/]")
                        .Border(BoxBorder.Rounded)
                        .BorderStyle(new Style(Color.Red));
                    AnsiConsole.Write(errorPanel);

                    if (!string.IsNullOrWhiteSpace(execution.ErrorStackTrace))
                    {
                        AnsiConsole.WriteLine();
                        AnsiConsole.MarkupLine("[dim]Stack Trace:[/]");
                        AnsiConsole.WriteLine(execution.ErrorStackTrace);
                    }
                }

                return 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.WriteException(ex);
                return 1;
            }
        }

        private string GetStatusMarkup(string status)
        {
            return status switch
            {
                "Success" => "[green]Success[/]",
                "Failed" => "[red]Failed[/]",
                "Running" => "[yellow]Running[/]",
                "Cancelled" => "[gray]Cancelled[/]",
                _ => status
            };
        }
    }

    // ============================================================================
    // HISTORY STATS
    // ============================================================================

    public class HistoryStatsSettings : CommandSettings
    {
        [Description("ID de configuración (opcional)")]
        [CommandArgument(0, "[config-id]")]
        public string? ConfigId { get; set; }

        [Description("Últimos N días")]
        [CommandOption("--days")]
        public int Days { get; set; } = 30;
    }

    public class HistoryStatsCommand : Command<HistoryStatsSettings>
    {
        public override int Execute(CommandContext context, HistoryStatsSettings settings)
        {
            DbManager.Initialize();

            try
            {
                var stats = ExecutionHistoryRepository.GetStats(settings.ConfigId, settings.Days);

                var title = string.IsNullOrWhiteSpace(settings.ConfigId)
                    ? $"Estadísticas de Ejecuciones (últimos {settings.Days} días)"
                    : $"Estadísticas: {settings.ConfigId} (últimos {settings.Days} días)";

                var panel = new Panel($"[bold cyan]{title}[/]")
                    .Border(BoxBorder.Double)
                    .BorderStyle(new Style(Color.Aqua));
                AnsiConsole.Write(panel);
                AnsiConsole.WriteLine();

                if (stats.TotalExecutions == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]No hay ejecuciones en el período especificado.[/]");
                    return 0;
                }

                var grid = new Grid();
                grid.AddColumn();
                grid.AddColumn();

                grid.AddRow("[bold cyan]Resumen de Ejecuciones[/]", "");
                grid.AddRow("  Total:", stats.TotalExecutions.ToString("N0"));
                grid.AddRow("  Exitosas:", $"[green]{stats.SuccessCount:N0}[/]");
                grid.AddRow("  Fallidas:", $"[red]{stats.FailedCount:N0}[/]");
                grid.AddRow("  En ejecución:", $"[yellow]{stats.RunningCount:N0}[/]");
                grid.AddRow("  Tasa de éxito:", $"{stats.SuccessRate:F1}%");

                grid.AddRow("");
                grid.AddRow("[bold cyan]Rendimiento[/]", "");
                var avgDuration = TimeSpan.FromMilliseconds(stats.AvgDurationMs);
                grid.AddRow("  Duración promedio:", $"{avgDuration.Hours}h {avgDuration.Minutes}m {avgDuration.Seconds}s");

                grid.AddRow("");
                grid.AddRow("[bold cyan]Filas Procesadas (Total)[/]", "");
                grid.AddRow("  Leídas:", stats.TotalRowsRead.ToString("N0"));
                grid.AddRow("  Insertadas:", stats.TotalRowsInserted.ToString("N0"));
                grid.AddRow("  Actualizadas:", stats.TotalRowsUpdated.ToString("N0"));
                grid.AddRow("  Eliminadas:", stats.TotalRowsDeleted.ToString("N0"));

                var totalProcessed = stats.TotalRowsInserted + stats.TotalRowsUpdated + stats.TotalRowsDeleted;
                grid.AddRow("  [bold]Total procesadas:[/]", $"[bold]{totalProcessed:N0}[/]");

                AnsiConsole.Write(grid);
                AnsiConsole.WriteLine();

                // Gráfico de barras para estado
                var barChart = new BarChart()
                    .Width(60)
                    .Label("[bold]Distribución de Estados[/]");

                if (stats.SuccessCount > 0)
                    barChart.AddItem("Exitosas", stats.SuccessCount, Color.Green);
                if (stats.FailedCount > 0)
                    barChart.AddItem("Fallidas", stats.FailedCount, Color.Red);
                if (stats.RunningCount > 0)
                    barChart.AddItem("En ejecución", stats.RunningCount, Color.Yellow);

                AnsiConsole.Write(barChart);

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
    // HISTORY CLEAR
    // ============================================================================

    public class HistoryClearSettings : CommandSettings
    {
        [Description("Eliminar registros más antiguos que N días")]
        [CommandOption("--older-than")]
        public int OlderThanDays { get; set; } = 90;

        [Description("No pedir confirmación")]
        [CommandOption("--force")]
        public bool Force { get; set; }
    }

    public class HistoryClearCommand : Command<HistoryClearSettings>
    {
        public override int Execute(CommandContext context, HistoryClearSettings settings)
        {
            DbManager.Initialize();

            try
            {
                var cutoffDate = DateTime.Now.AddDays(-settings.OlderThanDays);

                if (!settings.Force)
                {
                    var confirm = AnsiConsole.Confirm(
                        $"¿Eliminar ejecuciones anteriores a {cutoffDate:yyyy-MM-dd}?", false);
                    if (!confirm)
                    {
                        AnsiConsole.MarkupLine("[yellow]Cancelado.[/]");
                        return 0;
                    }
                }

                int deleted = ExecutionHistoryRepository.DeleteOlderThan(cutoffDate);

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
}
