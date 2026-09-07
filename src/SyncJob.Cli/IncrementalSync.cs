using System.Collections.Generic;

namespace SyncJob
{
    // ============================================================================
    // CONFIGURACIÓN DE SINCRONIZACIÓN INCREMENTAL
    // ============================================================================

    public class IncrementalConfig
    {
        /// <summary>
        /// Habilita sincronización incremental (solo cambios desde última ejecución)
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Estrategia de tracking de cambios
        /// </summary>
        public TrackingMode Mode { get; set; } = TrackingMode.Timestamp;

        /// <summary>
        /// Columna usada para tracking (ej: "FechaModificacion", "RowVersion")
        /// </summary>
        public string? TrackingColumn { get; set; }

        /// <summary>
        /// Tabla donde se guarda el estado de última sincronización
        /// Default: dbo.SyncJobTracking
        /// </summary>
        public string? TrackingTable { get; set; } = "dbo.SyncJobTracking";

        /// <summary>
        /// Nombre único del job para identificar el tracking (auto: Source.Table -> Dest.Table)
        /// </summary>
        public string? JobIdentifier { get; set; }

        /// <summary>
        /// Configuración para detección de deletes
        /// </summary>
        public DeleteDetectionConfig? DeleteDetection { get; set; }

        /// <summary>
        /// Forzar full refresh en la próxima ejecución (ignora tracking)
        /// </summary>
        public bool ForceFullRefresh { get; set; } = false;

        /// <summary>
        /// Columnas de clave primaria para merge (requerido para updates)
        /// </summary>
        public List<string>? PrimaryKeyColumns { get; set; }

        /// <summary>
        /// Estrategia de merge: Insert (solo nuevos), Upsert (insert + update), Full (insert + update + delete)
        /// </summary>
        public MergeStrategy MergeStrategy { get; set; } = MergeStrategy.Upsert;
    }

    public enum TrackingMode
    {
        /// <summary>
        /// Columna de tipo DateTime/DateTime2 (ej: FechaModificacion)
        /// WHERE FechaModificacion > @LastSyncTime
        /// </summary>
        Timestamp,

        /// <summary>
        /// Columna de tipo ROWVERSION/TIMESTAMP (binario monotónico)
        /// WHERE RowVersion > @LastRowVersion
        /// Más eficiente y confiable que Timestamp
        /// </summary>
        RowVersion,

        /// <summary>
        /// SQL Server Change Tracking (sys.change_tracking_*)
        /// Requiere: ALTER DATABASE SET CHANGE_TRACKING = ON
        /// Detecta INSERT, UPDATE, DELETE automáticamente
        /// </summary>
        ChangeTracking,

        /// <summary>
        /// SQL Server Change Data Capture (CDC)
        /// Más robusto que Change Tracking, guarda valores antiguos
        /// Requiere: Enterprise Edition o Standard 2016 SP1+
        /// </summary>
        ChangeDataCapture
    }

    public enum MergeStrategy
    {
        /// <summary>
        /// Solo INSERT de nuevos registros (ignora updates)
        /// </summary>
        Insert,

        /// <summary>
        /// INSERT nuevos + UPDATE existentes (basado en PK)
        /// </summary>
        Upsert,

        /// <summary>
        /// INSERT + UPDATE + DELETE (sincronización completa)
        /// </summary>
        Full
    }

    public class DeleteDetectionConfig
    {
        /// <summary>
        /// Habilita detección de registros eliminados
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Modo de detección
        /// </summary>
        public DeleteDetectionMode Mode { get; set; } = DeleteDetectionMode.SoftDelete;

        /// <summary>
        /// Para Soft Delete: columna que indica registro eliminado (ej: "IsDeleted", "Estado")
        /// </summary>
        public string? SoftDeleteColumn { get; set; }

        /// <summary>
        /// Para Soft Delete: valor que indica eliminación (ej: "1", "true", "INACTIVO")
        /// </summary>
        public string? SoftDeleteValue { get; set; } = "1";

        /// <summary>
        /// Para Comparison: compara PKs de origen vs destino
        /// Los registros en destino que no existen en origen se eliminan
        /// </summary>
        public bool UseComparison { get; set; } = false;
    }

    public enum DeleteDetectionMode
    {
        /// <summary>
        /// Columna que marca registros como eliminados (ej: IsDeleted = 1)
        /// </summary>
        SoftDelete,

        /// <summary>
        /// SQL Server Change Tracking/CDC detecta DELETEs automáticamente
        /// </summary>
        AutoDetect,

        /// <summary>
        /// Comparación de PKs: registros en destino que no existen en origen
        /// CUIDADO: Puede ser costoso para tablas grandes
        /// </summary>
        Comparison
    }

    // El motor incremental que vivia aqui - IncrementalSyncEngine y su
    // SyncTrackingState - se fue con la ruta que lo llamaba. Su tabla de tracking,
    // su BuildIncrementalQuery y su ExtractMaxTrackingValue los hace ahora
    // SyncJob.Core: SqlWatermarkStore guarda la marca de agua y el valor anterior,
    // que esto no guardaba; SourceSql arma la consulta; y StepRunner lee el maximo
    // de lo que quedo en el stage y no del origen, que es lo que evita que la marca
    // pase por delante de filas que nunca viajaron.
    //
    // Lo que queda es la forma del bloque Incremental del JSON, que sigue siendo lo
    // que se deserializa y lo que arman el servicio de Windows y el comando central.
}
