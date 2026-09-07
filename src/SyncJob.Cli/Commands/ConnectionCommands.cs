using Spectre.Console;
using Spectre.Console.Cli;
using SyncJob.Database;
using System;
using System.ComponentModel;
using Microsoft.Data.SqlClient;

namespace SyncJob.Commands
{
    // ============================================================================
    // CONNECTION ADD
    // ============================================================================

    public class ConnectionAddSettings : CommandSettings
    {
        [Description("ID único de la conexión")]
        [CommandArgument(0, "<connection-id>")]
        public string ConnectionId { get; set; } = string.Empty;

        [Description("Nombre del servidor SQL")]
        [CommandOption("--server")]
        public string? ServerName { get; set; }

        [Description("Nombre de la base de datos")]
        [CommandOption("--database")]
        public string? DatabaseName { get; set; }

        [Description("Usuario SQL")]
        [CommandOption("--username")]
        public string? Username { get; set; }

        [Description("Password SQL")]
        [CommandOption("--password")]
        public string? Password { get; set; }

        [Description("Confiar en certificado del servidor")]
        [CommandOption("--trust-cert")]
        public bool TrustServerCertificate { get; set; }

        [Description("Modo interactivo")]
        [CommandOption("--interactive")]
        public bool Interactive { get; set; }

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(ConnectionId))
                return ValidationResult.Error("ConnectionId es requerido");
            return ValidationResult.Success();
        }
    }

    public class ConnectionAddCommand : Command<ConnectionAddSettings>
    {
        public override int Execute(CommandContext context, ConnectionAddSettings settings)
        {
            DbManager.Initialize();

            try
            {
                if (ConnectionRepository.Exists(settings.ConnectionId))
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] La conexión '{settings.ConnectionId}' ya existe.");
                    return 1;
                }

                var conn = new ConnectionEntity
                {
                    ConnectionId = settings.ConnectionId,
                    DisplayName = settings.ConnectionId,
                    ServerType = "SqlServer"
                };

                if (settings.Interactive)
                {
                    AnsiConsole.MarkupLine("[bold cyan]Agregar Conexión Interactiva[/]");
                    AnsiConsole.WriteLine();

                    conn.DisplayName = AnsiConsole.Ask("Nombre descriptivo:", conn.DisplayName);
                    conn.ServerName = AnsiConsole.Ask<string>("Servidor SQL:");
                    conn.DatabaseName = AnsiConsole.Ask<string>("Base de datos:");
                    conn.Username = AnsiConsole.Ask<string>("Usuario (opcional, enter para Windows Auth):", string.Empty);

                    if (!string.IsNullOrWhiteSpace(conn.Username))
                    {
                        var password = AnsiConsole.Prompt(
                            new TextPrompt<string>("Password:")
                                .PromptStyle("red")
                                .Secret());

                        conn.PasswordEncrypted = ConnectionRepository.EncryptString(password);
                    }

                    conn.TrustServerCertificate = AnsiConsole.Confirm("Confiar en certificado del servidor?", false);
                }
                else
                {
                    conn.ServerName = settings.ServerName ?? throw new Exception("--server es requerido");
                    conn.DatabaseName = settings.DatabaseName ?? throw new Exception("--database es requerido");
                    conn.Username = settings.Username;
                    conn.TrustServerCertificate = settings.TrustServerCertificate;

                    if (!string.IsNullOrWhiteSpace(settings.Password))
                    {
                        conn.PasswordEncrypted = ConnectionRepository.EncryptString(settings.Password);
                    }
                }

                // Construir connection string
                var csBuilder = new SqlConnectionStringBuilder
                {
                    DataSource = conn.ServerName,
                    InitialCatalog = conn.DatabaseName,
                    TrustServerCertificate = conn.TrustServerCertificate,
                    Encrypt = conn.Encrypt
                };

                if (!string.IsNullOrWhiteSpace(conn.Username))
                {
                    csBuilder.UserID = conn.Username;
                    csBuilder.Password = conn.PasswordEncrypted != null
                        ? ConnectionRepository.DecryptString(conn.PasswordEncrypted)
                        : string.Empty;
                }
                else
                {
                    csBuilder.IntegratedSecurity = true;
                }

                conn.ConnectionStringEncrypted = ConnectionRepository.EncryptString(csBuilder.ToString());

                // Crear conexión
                ConnectionRepository.Create(conn);

                AnsiConsole.WriteLine();
                var panel = new Panel($"[green]✓[/] Conexión '[bold]{conn.ConnectionId}[/]' creada correctamente")
                    .Border(BoxBorder.Rounded)
                    .BorderStyle(new Style(Color.Green));
                AnsiConsole.Write(panel);

                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"Prueba la conexión: [cyan]connection test {conn.ConnectionId}[/]");

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
    // CONNECTION LIST
    // ============================================================================

    public class ConnectionListSettings : CommandSettings
    {
        [Description("Mostrar solo activas")]
        [CommandOption("--active-only")]
        public bool ActiveOnly { get; set; }
    }

    public class ConnectionListCommand : Command<ConnectionListSettings>
    {
        public override int Execute(CommandContext context, ConnectionListSettings settings)
        {
            DbManager.Initialize();

            try
            {
                bool? activeFilter = settings.ActiveOnly ? true : null;
                var connections = ConnectionRepository.ListAll(activeFilter);

                if (connections.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]No hay conexiones.[/]");
                    AnsiConsole.MarkupLine("Crea una con: [cyan]connection add <id> --interactive[/]");
                    return 0;
                }

                var table = new Table();
                table.Border(TableBorder.Rounded);
                table.AddColumn("[bold]ConnectionId[/]");
                table.AddColumn("[bold]Nombre[/]");
                table.AddColumn("[bold]Servidor[/]");
                table.AddColumn("[bold]Base de Datos[/]");
                table.AddColumn("[bold]Estado[/]");
                table.AddColumn("[bold]Último Test[/]");

                foreach (var c in connections)
                {
                    var status = c.IsActive ? "[green]✓ Activa[/]" : "[grey]✗ Inactiva[/]";
                    var testStatus = c.LastTestSuccess.HasValue
                        ? (c.LastTestSuccess.Value ? "[green]OK[/]" : "[red]FAIL[/]")
                        : "[grey]-[/]";

                    var lastTest = c.LastTestDate.HasValue
                        ? c.LastTestDate.Value.ToString("yyyy-MM-dd HH:mm")
                        : "-";

                    table.AddRow(
                        c.ConnectionId,
                        c.DisplayName,
                        c.ServerName,
                        c.DatabaseName,
                        status,
                        $"{lastTest} {testStatus}"
                    );
                }

                AnsiConsole.Write(table);
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"Total: [bold]{connections.Count}[/] conexión(es)");

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
    // CONNECTION TEST
    // ============================================================================

    public class ConnectionTestSettings : CommandSettings
    {
        [Description("ID de la conexión")]
        [CommandArgument(0, "<connection-id>")]
        public string ConnectionId { get; set; } = string.Empty;

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(ConnectionId))
                return ValidationResult.Error("ConnectionId es requerido");
            return ValidationResult.Success();
        }
    }

    public class ConnectionTestCommand : Command<ConnectionTestSettings>
    {
        public override int Execute(CommandContext context, ConnectionTestSettings settings)
        {
            DbManager.Initialize();

            try
            {
                var conn = ConnectionRepository.GetById(settings.ConnectionId);
                if (conn == null)
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] Conexión '{settings.ConnectionId}' no encontrada.");
                    return 1;
                }

                AnsiConsole.Status()
                    .Start($"Probando conexión a {conn.ServerName}...", ctx =>
                    {
                        ctx.Spinner(Spinner.Known.Dots);

                        var connectionString = conn.ConnectionStringEncrypted != null
                            ? ConnectionRepository.DecryptString(conn.ConnectionStringEncrypted)
                            : throw new Exception("Connection string no disponible");

                        using var sqlConn = new SqlConnection(connectionString);
                        sqlConn.Open();

                        // Query de prueba
                        using var cmd = new SqlCommand("SELECT @@VERSION, DB_NAME()", sqlConn);
                        using var reader = cmd.ExecuteReader();
                        reader.Read();

                        var version = reader.GetString(0);
                        var dbName = reader.GetString(1);

                        AnsiConsole.WriteLine();
                        AnsiConsole.MarkupLine("[green]✓ Conexión exitosa[/]");
                        AnsiConsole.WriteLine();
                        AnsiConsole.MarkupLine($"[bold]Servidor:[/] {conn.ServerName}");
                        AnsiConsole.MarkupLine($"[bold]Base de datos:[/] {dbName}");
                        AnsiConsole.MarkupLine($"[bold]Versión SQL:[/] {version.Split('\n')[0]}");
                    });

                return 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[red]✗ Error de conexión[/]");
                AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
                return 1;
            }
        }
    }

    // ============================================================================
    // CONNECTION DELETE
    // ============================================================================

    public class ConnectionDeleteSettings : CommandSettings
    {
        [Description("ID de la conexión")]
        [CommandArgument(0, "<connection-id>")]
        public string ConnectionId { get; set; } = string.Empty;

        [Description("No pedir confirmación")]
        [CommandOption("--force")]
        public bool Force { get; set; }

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(ConnectionId))
                return ValidationResult.Error("ConnectionId es requerido");
            return ValidationResult.Success();
        }
    }

    public class ConnectionDeleteCommand : Command<ConnectionDeleteSettings>
    {
        public override int Execute(CommandContext context, ConnectionDeleteSettings settings)
        {
            DbManager.Initialize();

            try
            {
                if (!ConnectionRepository.Exists(settings.ConnectionId))
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] Conexión '{settings.ConnectionId}' no encontrada.");
                    return 1;
                }

                if (!settings.Force)
                {
                    var confirm = AnsiConsole.Confirm($"¿Eliminar conexión '{settings.ConnectionId}'?", false);
                    if (!confirm)
                    {
                        AnsiConsole.MarkupLine("[yellow]Cancelado.[/]");
                        return 0;
                    }
                }

                ConnectionRepository.Delete(settings.ConnectionId);

                AnsiConsole.MarkupLine($"[green]✓[/] Conexión '{settings.ConnectionId}' eliminada correctamente.");
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
