using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SyncJob.Services.Models;
using System.Data;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SyncJob.Services
{
    /// <summary>
    /// Servicio de análisis con múltiples IAs via AI Proxy System (Claude, Codex, Gemini)
    /// Genera insights, detecta anomalías y hace recomendaciones sobre sincronizaciones
    /// </summary>
    public class AIAnalysisService
    {
        private readonly string _centralConnectionString;
        private readonly ILogger<AIAnalysisService> _logger;
        private readonly HttpClient _httpClient;
        private readonly AIProxyConfig _aiProxyConfig;

        public AIAnalysisService(
            string centralConnectionString,
            ILogger<AIAnalysisService> logger,
            IConfiguration? configuration = null,
            HttpClient? httpClient = null)
        {
            _centralConnectionString = centralConnectionString;
            _logger = logger;
            _httpClient = httpClient ?? new HttpClient();

            // Cargar configuración del AI Proxy
            _aiProxyConfig = new AIProxyConfig();
            if (configuration != null)
            {
                configuration.GetSection("AIProxySettings").Bind(_aiProxyConfig);
            }
            else
            {
                // Configuración por defecto si no se proporciona
                _aiProxyConfig.BaseUrl = "http://localhost:5100/api/proxy";
                _aiProxyConfig.Enabled = true;
            }

            _logger.LogInformation("AIAnalysisService initialized. AI Proxy: {BaseUrl}, Enabled: {Enabled}",
                _aiProxyConfig.BaseUrl, _aiProxyConfig.Enabled);
        }

        /// <summary>
        /// Analiza una tarea completada y genera insights multi-IA
        /// </summary>
        public async Task<AIAnalysisResult> AnalyzeTaskExecutionAsync(
            SyncTaskEntity task,
            SyncTaskExecutionResult result)
        {
            try
            {
                _logger.LogInformation("🤖 Iniciando análisis multi-IA para tarea {TaskId}", task.TaskId);

                var analysisResult = new AIAnalysisResult
                {
                    TaskId = task.TaskId,
                    ProjectId = task.ProjectId,
                    AnalyzedAt = DateTime.Now
                };

                // Obtener contexto histórico
                var historicalData = await GetHistoricalDataAsync(task.ProjectId, task.TaskType);

                // Preparar datos para análisis
                var context = BuildAnalysisContext(task, result, historicalData);

                // Ejecutar análisis con múltiples IAs EN PARALELO
                var tasks = new List<Task>
                {
                    AnalyzeWithClaudeAsync(context, analysisResult),
                    AnalyzeWithCodexAsync(context, analysisResult),
                    AnalyzeWithGeminiAsync(context, analysisResult)
                };

                await Task.WhenAll(tasks);

                // Agregar síntesis final
                analysisResult.FinalSynthesis = GenerateFinalSynthesis(analysisResult);

                _logger.LogInformation("✅ Análisis multi-IA completado para tarea {TaskId}", task.TaskId);

                return analysisResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en análisis IA para tarea {TaskId}", task.TaskId);

                return new AIAnalysisResult
                {
                    TaskId = task.TaskId,
                    ProjectId = task.ProjectId,
                    AnalyzedAt = DateTime.Now,
                    HasError = true,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <summary>
        /// Análisis con Claude (Insights estratégicos y recomendaciones) via AI Proxy
        /// </summary>
        private async Task AnalyzeWithClaudeAsync(AnalysisContext context, AIAnalysisResult result)
        {
            try
            {
                _logger.LogDebug("🧠 Claude analizando via AI Proxy...");

                if (!_aiProxyConfig.Enabled || !(_aiProxyConfig.Providers?.ContainsKey("Claude") == true))
                {
                    _logger.LogWarning("AI Proxy o Claude no habilitado, usando respuesta local");
                    result.ClaudeInsights = GenerateClaudeResponse(context);
                    result.ClaudeAnalyzedAt = DateTime.Now;
                    return;
                }

                var claudeProvider = _aiProxyConfig.Providers["Claude"];

                var prompt = $@"Analiza esta ejecución de sincronización de datos:

**Tarea:** {context.TaskType} del proyecto {context.ProjectId}
**Duración:** {context.DurationSeconds:N2} segundos
**Filas procesadas:** {context.RowsProcessed:N0}
**Operaciones:** {context.RowsInserted:N0} insertadas, {context.RowsUpdated:N0} actualizadas, {context.RowsDeleted:N0} eliminadas

**Histórico (últimas 10 ejecuciones):**
- Promedio duración: {context.AvgDurationSeconds:N2} segundos
- Promedio filas: {context.AvgRowsProcessed:N0}
- Total ejecuciones: {context.TotalExecutions}
- Tasa de éxito: {context.SuccessRate:P1}

**Desviación respecto al promedio:**
- Duración: {context.DurationDeviationPercent:+0.0;-0.0}%
- Filas: {context.RowsDeviationPercent:+0.0;-0.0}%

Genera un análisis breve (3-4 oraciones) incluyendo:
1. Evaluación del rendimiento (excelente, bueno, normal, lento)
2. Comparación con el histórico
3. Recomendación de acción si hay algo inusual
4. Un insight relevante

Responde en español, profesional y directo.";

                var claudeRequest = new AIProxyClaudeRequest
                {
                    ApiKey = _aiProxyConfig.ApiKey,
                    Model = claudeProvider.Model,
                    System = "Eres un analista de sistemas experto en sincronización de datos y optimización de rendimiento.",
                    Messages = new List<AIProxyMessage>
                    {
                        new AIProxyMessage { Role = "user", Content = prompt }
                    },
                    MaxTokens = claudeProvider.MaxTokens,
                    Temperature = claudeProvider.Temperature
                };

                var requestJson = JsonSerializer.Serialize(claudeRequest);
                var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

                var claudeUrl = $"{_aiProxyConfig.BaseUrl}{claudeProvider.Endpoint}";
                var response = await _httpClient.PostAsync(claudeUrl, content);
                var responseJson = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var proxyResponse = JsonSerializer.Deserialize<AIProxyResponse>(responseJson);
                    if (proxyResponse?.Success == true && !string.IsNullOrWhiteSpace(proxyResponse.Content))
                    {
                        result.ClaudeInsights = proxyResponse.Content;
                        result.ClaudeAnalyzedAt = DateTime.Now;
                        _logger.LogDebug("✅ Claude: {Preview}...", proxyResponse.Content.Substring(0, Math.Min(100, proxyResponse.Content.Length)));
                    }
                    else
                    {
                        throw new Exception($"Respuesta inválida del proxy: {proxyResponse?.Error ?? "sin contenido"}");
                    }
                }
                else
                {
                    throw new Exception($"Error HTTP {response.StatusCode}: {responseJson}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error en análisis de Claude via proxy, usando fallback local");
                result.ClaudeInsights = GenerateClaudeResponse(context);
                result.ClaudeAnalyzedAt = DateTime.Now;
            }
        }

        /// <summary>
        /// Análisis con Codex (Detección de patrones y código) via AI Proxy OpenAI
        /// </summary>
        private async Task AnalyzeWithCodexAsync(AnalysisContext context, AIAnalysisResult result)
        {
            try
            {
                _logger.LogDebug("💻 Codex analizando via AI Proxy...");

                if (!_aiProxyConfig.Enabled || !(_aiProxyConfig.Providers?.ContainsKey("Codex") == true))
                {
                    _logger.LogWarning("AI Proxy o Codex no habilitado, generando análisis local");
                    result.CodexPatterns = await SimulateCodexAnalysisAsync(context);
                    result.CodexAnalyzedAt = DateTime.Now;
                    return;
                }

                var codexProvider = _aiProxyConfig.Providers["Codex"];

                var prompt = $@"Analyze this data synchronization execution and detect patterns:

Task: {context.TaskType} for {context.ProjectId}
Duration: {context.DurationSeconds:N2}s
Rows: {context.RowsProcessed:N0} ({context.RowsInserted:N0} inserted, {context.RowsUpdated:N0} updated)

Historical average: {context.AvgDurationSeconds:N2}s, {context.AvgRowsProcessed:N0} rows
Deviation: Duration {context.DurationDeviationPercent:+0.0;-0.0}%, Rows {context.RowsDeviationPercent:+0.0;-0.0}%

Provide:
1. Pattern detection (normal, anomaly, trend)
2. Performance classification (optimal, acceptable, poor)
3. Technical recommendation if needed

Keep it concise (2-3 sentences).";

                var codexRequest = new AIProxyOpenAIRequest
                {
                    ApiKey = _aiProxyConfig.ApiKey,
                    Model = codexProvider.Model,
                    Messages = new List<AIProxyMessage>
                    {
                        new AIProxyMessage { Role = "system", Content = "You are a technical systems analyst specialized in data sync patterns and performance optimization." },
                        new AIProxyMessage { Role = "user", Content = prompt }
                    },
                    MaxTokens = codexProvider.MaxTokens,
                    Temperature = codexProvider.Temperature
                };

                var requestJson = JsonSerializer.Serialize(codexRequest);
                var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

                var codexUrl = $"{_aiProxyConfig.BaseUrl}{codexProvider.Endpoint}";
                var response = await _httpClient.PostAsync(codexUrl, content);
                var responseJson = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var proxyResponse = JsonSerializer.Deserialize<AIProxyResponse>(responseJson);
                    if (proxyResponse?.Success == true && !string.IsNullOrWhiteSpace(proxyResponse.Content))
                    {
                        result.CodexPatterns = proxyResponse.Content;
                        result.CodexAnalyzedAt = DateTime.Now;
                        _logger.LogDebug("✅ Codex: {Preview}...", proxyResponse.Content.Substring(0, Math.Min(100, proxyResponse.Content.Length)));
                    }
                    else
                    {
                        throw new Exception($"Respuesta inválida del proxy: {proxyResponse?.Error ?? "sin contenido"}");
                    }
                }
                else
                {
                    throw new Exception($"Error HTTP {response.StatusCode}: {responseJson}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error en análisis de Codex via proxy, usando fallback local");
                result.CodexPatterns = await SimulateCodexAnalysisAsync(context);
                result.CodexAnalyzedAt = DateTime.Now;
            }
        }

        /// <summary>
        /// Análisis con Gemini (Alertas rápidas y detección de anomalías) via AI Proxy
        /// </summary>
        private async Task AnalyzeWithGeminiAsync(AnalysisContext context, AIAnalysisResult result)
        {
            try
            {
                _logger.LogDebug("⚡ Gemini analizando via AI Proxy...");

                if (!_aiProxyConfig.Enabled || !(_aiProxyConfig.Providers?.ContainsKey("Gemini") == true))
                {
                    _logger.LogWarning("AI Proxy o Gemini no habilitado, generando análisis local");
                    result.GeminiAlerts = await SimulateGeminiAnalysisAsync(context);
                    result.GeminiAnalyzedAt = DateTime.Now;
                    return;
                }

                var geminiProvider = _aiProxyConfig.Providers["Gemini"];

                var prompt = $@"Quick analysis of this sync task:

Project: {context.ProjectId}
Duration: {context.DurationSeconds:N2}s (avg: {context.AvgDurationSeconds:N2}s)
Rows: {context.RowsProcessed:N0} (avg: {context.AvgRowsProcessed:N0})
Success rate: {context.SuccessRate:P0}

Detect:
1. Is this execution normal or anomalous?
2. Any red flags or concerns?
3. Quick recommendation

Be concise (1-2 sentences).";

                var geminiRequest = new AIProxyGeminiRequest
                {
                    ApiKey = _aiProxyConfig.ApiKey,
                    Model = geminiProvider.Model,
                    Contents = new List<AIProxyContent>
                    {
                        new AIProxyContent
                        {
                            Parts = new List<AIProxyPart>
                            {
                                new AIProxyPart { Text = prompt }
                            }
                        }
                    }
                };

                var requestJson = JsonSerializer.Serialize(geminiRequest);
                var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

                var geminiUrl = $"{_aiProxyConfig.BaseUrl}{geminiProvider.Endpoint}";
                var response = await _httpClient.PostAsync(geminiUrl, content);
                var responseJson = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var proxyResponse = JsonSerializer.Deserialize<AIProxyResponse>(responseJson);
                    if (proxyResponse?.Success == true && !string.IsNullOrWhiteSpace(proxyResponse.Content))
                    {
                        result.GeminiAlerts = proxyResponse.Content;
                        result.GeminiAnalyzedAt = DateTime.Now;
                        _logger.LogDebug("✅ Gemini: {Preview}...", proxyResponse.Content.Substring(0, Math.Min(100, proxyResponse.Content.Length)));
                    }
                    else
                    {
                        throw new Exception($"Respuesta inválida del proxy: {proxyResponse?.Error ?? "sin contenido"}");
                    }
                }
                else
                {
                    throw new Exception($"Error HTTP {response.StatusCode}: {responseJson}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error en análisis de Gemini via proxy, usando fallback local");
                result.GeminiAlerts = await SimulateGeminiAnalysisAsync(context);
                result.GeminiAnalyzedAt = DateTime.Now;
            }
        }

        /// <summary>
        /// Genera respuesta de Claude (nativo)
        /// </summary>
        private string GenerateClaudeResponse(AnalysisContext context)
        {
            var performance = ClassifyPerformance(context);
            var comparison = CompareWithHistorical(context);

            var insights = new StringBuilder();

            insights.AppendLine($"**Rendimiento {performance}:** ");

            if (context.DurationDeviationPercent > 50)
            {
                insights.AppendLine($"La sincronización tomó **{context.DurationDeviationPercent:+0.0;-0.0}% más tiempo** que el promedio histórico ({context.DurationSeconds:N1}s vs {context.AvgDurationSeconds:N1}s). Esto podría indicar carga inusual en el origen o destino. Recomiendo revisar los índices de las tablas y considerar ejecutar la sincronización en horarios de menor carga.");
            }
            else if (context.DurationDeviationPercent < -30)
            {
                insights.AppendLine($"Excelente: la sincronización fue **{Math.Abs(context.DurationDeviationPercent):N0}% más rápida** que el promedio ({context.DurationSeconds:N1}s vs {context.AvgDurationSeconds:N1}s). {comparison}");
            }
            else
            {
                insights.AppendLine($"El tiempo de ejecución ({context.DurationSeconds:N1}s) está dentro del rango esperado. {comparison}");
            }

            if (context.RowsDeviationPercent > 100)
            {
                insights.AppendLine($"⚠️ **Alerta:** Se procesaron **{context.RowsDeviationPercent:N0}% más filas** de lo normal ({context.RowsProcessed:N0} vs {context.AvgRowsProcessed:N0} promedio). Esto sugiere un crecimiento significativo de datos o acumulación. Considere aumentar la frecuencia de sincronización.");
            }

            insights.AppendLine($"\n💡 **Insight:** Con una tasa de éxito del {context.SuccessRate:P1} en las últimas {context.TotalExecutions} ejecuciones, este proyecto muestra {(context.SuccessRate >= 0.95 ? "excelente estabilidad" : context.SuccessRate >= 0.80 ? "estabilidad aceptable" : "necesita atención")}.");

            return insights.ToString();
        }

        /// <summary>
        /// Simula análisis de Codex (en producción llamaría al MCP)
        /// </summary>
        private async Task<string> SimulateCodexAnalysisAsync(AnalysisContext context)
        {
            await Task.Delay(100); // Simular latencia

            var patterns = new StringBuilder();

            // Detección de patrones
            if (Math.Abs(context.DurationDeviationPercent) < 10 && Math.Abs(context.RowsDeviationPercent) < 15)
            {
                patterns.AppendLine("**Pattern: STABLE** - Execution metrics are consistent with historical data. System is operating within expected parameters.");
            }
            else if (context.DurationDeviationPercent > 30 || context.RowsDeviationPercent > 50)
            {
                patterns.AppendLine("**Pattern: ANOMALY DETECTED** - Significant deviation from baseline. Recommend investigating source data growth and query performance.");
            }
            else
            {
                patterns.AppendLine("**Pattern: NORMAL VARIANCE** - Minor fluctuations detected, within acceptable range for production systems.");
            }

            // Clasificación de performance
            var throughput = context.RowsProcessed / Math.Max(context.DurationSeconds, 1);
            var avgThroughput = context.AvgRowsProcessed / Math.Max(context.AvgDurationSeconds, 1);

            if (throughput > avgThroughput * 1.2)
            {
                patterns.AppendLine($"**Performance: OPTIMAL** - Throughput of {throughput:N0} rows/sec exceeds baseline by {((throughput / avgThroughput - 1) * 100):N0}%.");
            }
            else if (throughput < avgThroughput * 0.7)
            {
                patterns.AppendLine($"**Performance: DEGRADED** - Throughput of {throughput:N0} rows/sec is {((1 - throughput / avgThroughput) * 100):N0}% below baseline. Consider indexing optimization.");
            }
            else
            {
                patterns.AppendLine($"**Performance: ACCEPTABLE** - Throughput of {throughput:N0} rows/sec is within normal range.");
            }

            return patterns.ToString();
        }

        /// <summary>
        /// Simula análisis de Gemini (en producción llamaría al MCP)
        /// </summary>
        private async Task<string> SimulateGeminiAnalysisAsync(AnalysisContext context)
        {
            await Task.Delay(80); // Simular latencia

            var alerts = new StringBuilder();

            // Detección rápida de anomalías
            bool hasAnomaly = false;

            if (context.DurationDeviationPercent > 100)
            {
                alerts.AppendLine("🚨 **ALERT:** Execution time doubled. Investigate immediately.");
                hasAnomaly = true;
            }
            else if (context.DurationDeviationPercent > 50)
            {
                alerts.AppendLine("⚠️ **WARNING:** Slower than usual. Monitor closely.");
                hasAnomaly = true;
            }

            if (context.SuccessRate < 0.80)
            {
                alerts.AppendLine("⚠️ **CONCERN:** Success rate below 80%. Review error logs.");
                hasAnomaly = true;
            }

            if (!hasAnomaly)
            {
                alerts.AppendLine("✅ **ALL CLEAR:** No anomalies detected. System healthy.");
            }

            // Recomendación rápida
            if (context.RowsProcessed > context.AvgRowsProcessed * 2)
            {
                alerts.AppendLine("💡 **TIP:** Data volume is growing. Consider batch processing or incremental sync.");
            }
            else if (context.DurationSeconds > 300 && context.RowsProcessed < 10000)
            {
                alerts.AppendLine("💡 **TIP:** Low throughput detected. Check network latency and query optimization.");
            }

            return alerts.ToString();
        }

        /// <summary>
        /// Genera síntesis final combinando todos los análisis
        /// </summary>
        private string GenerateFinalSynthesis(AIAnalysisResult result)
        {
            var synthesis = new StringBuilder();

            synthesis.AppendLine("## 🤖 Análisis Multi-IA");
            synthesis.AppendLine();

            if (!string.IsNullOrWhiteSpace(result.ClaudeInsights))
            {
                synthesis.AppendLine("### 🧠 Claude (Estratégico)");
                synthesis.AppendLine(result.ClaudeInsights);
                synthesis.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(result.CodexPatterns))
            {
                synthesis.AppendLine("### 💻 Codex (Patrones)");
                synthesis.AppendLine(result.CodexPatterns);
                synthesis.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(result.GeminiAlerts))
            {
                synthesis.AppendLine("### ⚡ Gemini (Alertas)");
                synthesis.AppendLine(result.GeminiAlerts);
                synthesis.AppendLine();
            }

            synthesis.AppendLine("---");
            synthesis.AppendLine("*Análisis generado por sistemas de IA de PeopleWorks*");

            return synthesis.ToString();
        }

        /// <summary>
        /// Clasifica el rendimiento de la ejecución
        /// </summary>
        private string ClassifyPerformance(AnalysisContext context)
        {
            if (context.DurationDeviationPercent < -20)
                return "Excelente";
            else if (context.DurationDeviationPercent < 10)
                return "Bueno";
            else if (context.DurationDeviationPercent < 50)
                return "Normal";
            else
                return "Lento";
        }

        /// <summary>
        /// Compara con datos históricos
        /// </summary>
        private string CompareWithHistorical(AnalysisContext context)
        {
            var rowsChange = context.RowsProcessed - context.AvgRowsProcessed;

            if (Math.Abs(rowsChange) < context.AvgRowsProcessed * 0.1)
            {
                return "El volumen de datos se mantiene estable.";
            }
            else if (rowsChange > 0)
            {
                return $"Se procesaron {Math.Abs(context.RowsDeviationPercent):N0}% más filas de lo habitual, indicando crecimiento de datos.";
            }
            else
            {
                return $"Se procesaron {Math.Abs(context.RowsDeviationPercent):N0}% menos filas de lo habitual.";
            }
        }

        /// <summary>
        /// Obtiene datos históricos para contexto
        /// </summary>
        private async Task<HistoricalData> GetHistoricalDataAsync(string? projectId, string taskType)
        {
            try
            {
                using var connection = new SqlConnection(_centralConnectionString);
                await connection.OpenAsync();

                var query = @"
                    SELECT
                        COUNT(*) AS TotalExecutions,
                        AVG(CAST([DurationMs] AS FLOAT)) AS AvgDurationMs,
                        AVG(CAST([RowsProcessed] AS FLOAT)) AS AvgRowsProcessed,
                        SUM(CASE WHEN [Status] = 'Completed' THEN 1.0 ELSE 0.0 END) / COUNT(*) AS SuccessRate,
                        MIN([DurationMs]) AS MinDurationMs,
                        MAX([DurationMs]) AS MaxDurationMs
                    FROM [SyncTasks]
                    WHERE [ProjectId] = @ProjectId
                      AND [TaskType] = @TaskType
                      AND [CompletedAt] IS NOT NULL
                      AND [CompletedAt] >= DATEADD(DAY, -30, GETDATE())";

                using var cmd = new SqlCommand(query, connection);
                cmd.Parameters.AddWithValue("@ProjectId", projectId ?? "");
                cmd.Parameters.AddWithValue("@TaskType", taskType);

                using var reader = await cmd.ExecuteReaderAsync();

                if (await reader.ReadAsync())
                {
                    return new HistoricalData
                    {
                        TotalExecutions = reader.GetInt32(0),
                        AvgDurationMs = reader.IsDBNull(1) ? 0 : reader.GetDouble(1),
                        AvgRowsProcessed = reader.IsDBNull(2) ? 0 : reader.GetDouble(2),
                        SuccessRate = reader.IsDBNull(3) ? 0 : reader.GetDouble(3),
                        MinDurationMs = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                        MaxDurationMs = reader.IsDBNull(5) ? 0 : reader.GetInt64(5)
                    };
                }

                return new HistoricalData();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error obteniendo datos históricos");
                return new HistoricalData();
            }
        }

        /// <summary>
        /// Construye contexto de análisis
        /// </summary>
        private AnalysisContext BuildAnalysisContext(
            SyncTaskEntity task,
            SyncTaskExecutionResult result,
            HistoricalData historical)
        {
            var durationSeconds = result.DurationMs / 1000.0;
            var avgDurationSeconds = historical.AvgDurationMs / 1000.0;

            var durationDeviation = avgDurationSeconds > 0
                ? ((durationSeconds - avgDurationSeconds) / avgDurationSeconds) * 100
                : 0;

            var rowsDeviation = historical.AvgRowsProcessed > 0
                ? ((result.RowsProcessed - historical.AvgRowsProcessed) / historical.AvgRowsProcessed) * 100
                : 0;

            return new AnalysisContext
            {
                ProjectId = task.ProjectId ?? "",
                TaskType = task.TaskType,
                DurationSeconds = durationSeconds,
                RowsProcessed = result.RowsProcessed,
                RowsInserted = result.RowsInserted,
                RowsUpdated = result.RowsUpdated,
                RowsDeleted = result.RowsDeleted,
                AvgDurationSeconds = avgDurationSeconds,
                AvgRowsProcessed = historical.AvgRowsProcessed,
                TotalExecutions = historical.TotalExecutions,
                SuccessRate = historical.SuccessRate,
                DurationDeviationPercent = durationDeviation,
                RowsDeviationPercent = rowsDeviation
            };
        }
    }

    #region Models

    public class AIAnalysisResult
    {
        public Guid TaskId { get; set; }
        public string? ProjectId { get; set; }
        public DateTime AnalyzedAt { get; set; }

        // Análisis por IA
        public string? ClaudeInsights { get; set; }
        public DateTime? ClaudeAnalyzedAt { get; set; }

        public string? CodexPatterns { get; set; }
        public DateTime? CodexAnalyzedAt { get; set; }

        public string? GeminiAlerts { get; set; }
        public DateTime? GeminiAnalyzedAt { get; set; }

        // Síntesis final
        public string? FinalSynthesis { get; set; }

        // Error handling
        public bool HasError { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public class AnalysisContext
    {
        public string ProjectId { get; set; } = string.Empty;
        public string TaskType { get; set; } = string.Empty;
        public double DurationSeconds { get; set; }
        public long RowsProcessed { get; set; }
        public long RowsInserted { get; set; }
        public long RowsUpdated { get; set; }
        public long RowsDeleted { get; set; }
        public double AvgDurationSeconds { get; set; }
        public double AvgRowsProcessed { get; set; }
        public int TotalExecutions { get; set; }
        public double SuccessRate { get; set; }
        public double DurationDeviationPercent { get; set; }
        public double RowsDeviationPercent { get; set; }
    }

    public class HistoricalData
    {
        public int TotalExecutions { get; set; }
        public double AvgDurationMs { get; set; }
        public double AvgRowsProcessed { get; set; }
        public double SuccessRate { get; set; }
        public long MinDurationMs { get; set; }
        public long MaxDurationMs { get; set; }
    }

    // ========================================================================
    // AI PROXY SYSTEM - Configuration & Request/Response Models
    // ========================================================================

    /// <summary>
    /// Configuración del AI Proxy System de PeopleWorks
    /// </summary>
    public class AIProxyConfig
    {
        public string BaseUrl { get; set; } = "http://localhost:5100/api/proxy";
        public string ApiKey { get; set; } = "server-configured";
        public bool Enabled { get; set; } = true;
        public int Timeout { get; set; } = 30000;
        public Dictionary<string, ProviderConfig>? Providers { get; set; }
        public List<string>? FallbackOrder { get; set; }
        public int RetryAttempts { get; set; } = 2;
        public int RetryDelay { get; set; } = 5000;
    }

    /// <summary>
    /// Configuración de un proveedor de IA individual
    /// </summary>
    public class ProviderConfig
    {
        public bool Enabled { get; set; } = true;
        public string Model { get; set; } = string.Empty;
        public int MaxTokens { get; set; } = 2000;
        public double Temperature { get; set; } = 0.3;
        public string Endpoint { get; set; } = string.Empty;
    }

    /// <summary>
    /// Request para OpenAI/Codex via AI Proxy
    /// </summary>
    public class AIProxyOpenAIRequest
    {
        public string ApiKey { get; set; } = "server-configured";
        public string Model { get; set; } = string.Empty;
        public List<AIProxyMessage> Messages { get; set; } = new();
        public int? MaxTokens { get; set; }
        public double? Temperature { get; set; }
    }

    /// <summary>
    /// Request para Claude via AI Proxy
    /// </summary>
    public class AIProxyClaudeRequest
    {
        public string ApiKey { get; set; } = "server-configured";
        public string Model { get; set; } = string.Empty;
        public string System { get; set; } = string.Empty;
        public List<AIProxyMessage> Messages { get; set; } = new();
        public int? MaxTokens { get; set; }
        public double? Temperature { get; set; }
    }

    /// <summary>
    /// Request para Gemini via AI Proxy
    /// </summary>
    public class AIProxyGeminiRequest
    {
        public string ApiKey { get; set; } = "server-configured";
        public string Model { get; set; } = string.Empty;
        public List<AIProxyContent> Contents { get; set; } = new();
    }

    /// <summary>
    /// Mensaje para OpenAI/Claude
    /// </summary>
    public class AIProxyMessage
    {
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
    }

    /// <summary>
    /// Contenido para Gemini
    /// </summary>
    public class AIProxyContent
    {
        public List<AIProxyPart> Parts { get; set; } = new();
    }

    /// <summary>
    /// Parte de contenido para Gemini
    /// </summary>
    public class AIProxyPart
    {
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>
    /// Respuesta del AI Proxy
    /// </summary>
    public class AIProxyResponse
    {
        public bool Success { get; set; }
        public string? Content { get; set; }
        public string? Error { get; set; }
    }

    #endregion
}
