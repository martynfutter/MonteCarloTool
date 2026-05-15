// Class_resultsDatabase.cs
//
// Migrated from Microsoft Access (ODBC) to SQLite (System.Data.SQLite).
//
// Changes from original:
//   - OdbcConnection/OdbcCommand/OdbcException replaced with SQLiteConnection/SQLiteCommand/SQLiteException
//   - Database file changed from mc.accdb to mc.db
//   - DELETE * FROM  ->  DELETE FROM
//   - DROP TABLE X   ->  DROP TABLE IF EXISTS X
//   - DATE()         ->  date('now')
//   - SELECT x INTO NewTable FROM  ->  CREATE TABLE NewTable AS SELECT x FROM
//   - TEXT(255), DOUBLE  ->  TEXT, REAL  (SQLite ignores length constraints)
//   - Access saved queries [102 Sampled Pars] and [116 Statistics Summary] replaced
//     by SQLite views vw_sampled_pars and vw_statistics_summary (see CreateKSViews)
//   - Bulk insert methods wrapped in transactions for performance
//   - Parameterised queries used for all INSERT statements
//   - Missing comma bug fixed in makeObservationsTable()
//   - EnsureSchema() creates all permanent tables and KS views on first run
//
// NuGet dependency: System.Data.SQLite (install via NuGet — search for System.Data.SQLite)
//
// NOTE: Class_interactWithDatabasecs.cs contains a conflicting stub definition of
// resultsDatabase that is not included in the .csproj compile list. It should be
// deleted or renamed to avoid confusion.
//
// NOTE: MCParameters.databaseFileName default value should be changed from
// ".\mc.accdb" to ".\mc.db" in Class_MCParameters.cs.
//
// Interactive analysis queries (Access queries 101-202, the KS sensitivity
// analysis chain) are now implemented as SQLite views (see CreateKSViews).
// They can be run interactively using any SQLite browser tool such as
// DB Browser for SQLite (https://sqlitebrowser.org/).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Data.SQLite;

namespace MC
{
    class resultsDatabase
    {
        protected SQLiteConnection localConnection;

        public resultsDatabase()
        {
            localConnection = new SQLiteConnection();
        }

        // -------------------------------------------------------------------------
        // Connection helpers
        // -------------------------------------------------------------------------

        private string GetConnectionString()
        {
            string path = Directory.GetCurrentDirectory();
            MCParameters.databaseFileName = Path.Combine(path, "mc.db");
            return $"Data Source={MCParameters.databaseFileName}";
        }

        private bool OpenConnection()
        {
            localConnection.ConnectionString = GetConnectionString();
            try
            {
                if (localConnection.State != ConnectionState.Open)
                    localConnection.Open();
                return true;
            }
            catch (SQLiteException ex)
            {
                Console.WriteLine(ex.Message);
                return false;
            }
        }

        private void CloseConnection()
        {
            if (localConnection.State == ConnectionState.Open)
                localConnection.Close();
        }

        // -------------------------------------------------------------------------
        // Schema initialisation
        // -------------------------------------------------------------------------

        /// <summary>
        /// Creates all permanent tables and KS analysis views if they do not already
        /// exist. Safe to call on every run — uses IF NOT EXISTS throughout.
        /// Called automatically from cleanUp() so no separate initialisation step
        /// is needed in calling code.
        /// </summary>
        private void EnsureSchema()
        {
            // --- Permanent tables ------------------------------------------------

            executeSQLCommand(@"
                CREATE TABLE IF NOT EXISTS ParNames (
                    ParID    INTEGER PRIMARY KEY,
                    ParName  TEXT
                )");

            executeSQLCommand(@"
                CREATE TABLE IF NOT EXISTS ParList (
                    ID           INTEGER PRIMARY KEY AUTOINCREMENT,
                    RunID        INTEGER,
                    ParID        INTEGER,
                    TextValue    TEXT,
                    NumericValue REAL
                )");

            executeSQLCommand(@"
                CREATE TABLE IF NOT EXISTS SortedParameters (
                    ID             INTEGER PRIMARY KEY AUTOINCREMENT,
                    ParID          INTEGER,
                    ParameterValue REAL,
                    RunID          INTEGER
                )");

            executeSQLCommand(@"
                CREATE TABLE IF NOT EXISTS CoefficientWeights (
                    CoefficientName   TEXT,
                    CoefficientWeight REAL
                )");

            // --- KS sensitivity analysis views -----------------------------------
            CreateKSViews();
        }

        /// <summary>
        /// Creates SQLite views that implement the Kolmogorov-Smirnov parameter
        /// sensitivity analysis. These replace the Access saved queries 101-116.
        ///
        /// View chain:
        ///   vw_par_stats             [101 Par Stats]
        ///   vw_sampled_pars          [102 Sampled Pars]
        ///   vw_parameter_ranges      [104 Parameter Ranges]
        ///   vw_parameters_with_offsets [105 Parameters with Offsets]
        ///   vw_observed_theoretical  [106 Observed And Theoretical Offsets]
        ///   vw_test_statistic        [107 Test Statistic]
        ///   vw_ks_d_statistic        [108 KS D Statistic]
        ///   vw_ks_d_with_range       [109 KS D Statistic with RunTerm]
        ///   vw_ks_d_and_z            [110 KS D and z]
        ///   vw_ks_p                  [111-114 p-value terms combined]
        ///   vw_ks_with_names         [115 KS D z and P with Names]
        ///   vw_statistics_summary    [116 Statistics Summary]
        ///
        /// All views use CREATE VIEW IF NOT EXISTS so they are safe to call repeatedly.
        /// </summary>
        private void CreateKSViews()
        {
            // [101 Par Stats]
            // Aggregate min/avg/max of each parameter across all runs.
            executeSQLCommand(@"
                CREATE VIEW IF NOT EXISTS vw_par_stats AS
                SELECT  ParID,
                        MIN(NumericValue) AS MinOfNumericValue,
                        AVG(NumericValue) AS AvgOfNumericValue,
                        MAX(NumericValue) AS MaxOfNumericValue
                FROM    ParList
                GROUP BY ParID");

            // [102 Sampled Pars]
            // Filter to only parameters that actually varied between runs.
            // Referenced directly from processParameterData() as vw_sampled_pars.
            executeSQLCommand(@"
                CREATE VIEW IF NOT EXISTS vw_sampled_pars AS
                SELECT  ParID, MinOfNumericValue, AvgOfNumericValue, MaxOfNumericValue
                FROM    vw_par_stats
                WHERE   MinOfNumericValue <> MaxOfNumericValue");

            // [104 Parameter Ranges]
            // Row ID bounds and value bounds for each parameter in SortedParameters.
            executeSQLCommand(@"
                CREATE VIEW IF NOT EXISTS vw_parameter_ranges AS
                SELECT  ParID,
                        MIN(ID) AS MinOfID,
                        MAX(ID) AS MaxOfID,
                        MIN(ParameterValue) AS MinOfParameterValue,
                        MAX(ParameterValue) AS MaxOfParameterValue
                FROM    SortedParameters
                GROUP BY ParID");

            // [105 Parameters with Offsets]
            // Rank offset of each sorted parameter value within its parameter group.
            executeSQLCommand(@"
                CREATE VIEW IF NOT EXISTS vw_parameters_with_offsets AS
                SELECT  r.ParID,
                        s.ID,
                        s.ID - r.MinOfID                      AS Offset,
                        r.MaxOfID - r.MinOfID                 AS Runs,
                        s.ParameterValue,
                        r.MinOfParameterValue,
                        r.MaxOfParameterValue
                FROM    vw_parameter_ranges r
                INNER JOIN SortedParameters s ON r.ParID = s.ParID");

            // [106 Observed And Theoretical Offsets]
            // Empirical (observed) CDF vs theoretical uniform CDF.
            executeSQLCommand(@"
                CREATE VIEW IF NOT EXISTS vw_observed_theoretical AS
                SELECT  ParID, ID, Offset, MinOfParameterValue, ParameterValue, MaxOfParameterValue,
                        CAST(Offset AS REAL) / CAST(Runs AS REAL)         AS ObservedCDF,
                        (ParameterValue     - MinOfParameterValue) /
                        (MaxOfParameterValue - MinOfParameterValue)        AS TheoreticalCDF,
                        Runs
                FROM    vw_parameters_with_offsets");

            // [107 Test Statistic]
            // Absolute difference between empirical and theoretical CDFs.
            executeSQLCommand(@"
                CREATE VIEW IF NOT EXISTS vw_test_statistic AS
                SELECT  ParID, ObservedCDF, TheoreticalCDF,
                        ABS(TheoreticalCDF - ObservedCDF) AS Test,
                        Runs
                FROM    vw_observed_theoretical");

            // [108 KS D Statistic]
            // Maximum CDF difference (D) per parameter — the KS test statistic.
            executeSQLCommand(@"
                CREATE VIEW IF NOT EXISTS vw_ks_d_statistic AS
                SELECT  ParID, MAX(Test) AS D, Runs
                FROM    vw_test_statistic
                GROUP BY ParID, Runs");

            // [109 KS D Statistic with RunTerm]
            // Joins back to find the TheoreticalCDF (xRange) at the D-statistic point.
            // Access formula: Sqr(Runs*Runs/(2*Runs)) simplifies to SQRT(Runs/2).
            executeSQLCommand(@"
                CREATE VIEW IF NOT EXISTS vw_ks_d_with_range AS
                SELECT  k.ParID,
                        k.D,
                        t.TheoreticalCDF                      AS xRange,
                        k.Runs,
                        SQRT(CAST(k.Runs AS REAL) / 2.0)      AS RunTerm
                FROM    vw_test_statistic t
                INNER JOIN vw_ks_d_statistic k
                    ON  k.D = t.Test AND t.ParID = k.ParID");

            // [110 KS D and z]
            // Convert D statistic to z score.
            executeSQLCommand(@"
                CREATE VIEW IF NOT EXISTS vw_ks_d_and_z AS
                SELECT  ParID, D, xRange,
                        D * (RunTerm + 0.12 + 0.11 / RunTerm) AS z
                FROM    vw_ks_d_with_range");

            // [111-114 pTerm1 through p]
            // p-value via KS distribution approximation, truncated at 4 terms:
            //   p = 2 * sum_{k=1}^{4} (-1)^(k+1) * exp(-2 * k^2 * z^2)
            // The Access queries built this incrementally; combined here into one view.
            executeSQLCommand(@"
                CREATE VIEW IF NOT EXISTS vw_ks_p AS
                SELECT  ParID, D, xRange, z,
                          exp(-2.0  * z * z)
                        - exp(-8.0  * z * z)
                        + exp(-18.0 * z * z)
                        - exp(-32.0 * z * z) AS p
                FROM    vw_ks_d_and_z");

            // [115 KS D z and P with Names]
            // Attach parameter names from ParNames.
            executeSQLCommand(@"
                CREATE VIEW IF NOT EXISTS vw_ks_with_names AS
                SELECT  n.ParName, k.ParID, k.D, k.xRange, k.z, k.p
                FROM    ParNames n
                INNER JOIN vw_ks_p k ON n.ParID = k.ParID
                ORDER BY k.ParID");

            // [116 Statistics Summary]
            // Final sensitivity summary joined with sampled-parameter value ranges.
            // Referenced from createParameterSensitivitySummaryTable() as vw_statistics_summary.
            executeSQLCommand(@"
                CREATE VIEW IF NOT EXISTS vw_statistics_summary AS
                SELECT  k.ParName, k.ParID, k.D,
                        s.MinOfNumericValue, s.MaxOfNumericValue,
                        k.xRange, k.z, k.p
                FROM    vw_sampled_pars s
                INNER JOIN vw_ks_with_names k ON s.ParID = k.ParID");
        }

        // -------------------------------------------------------------------------
        // Public database management methods
        // -------------------------------------------------------------------------

        public void cleanUp()
        {
            if (!OpenConnection())
            {
                Console.WriteLine("Could not clean up database");
                return;
            }

            // Ensure permanent tables and KS views exist before we touch anything
            EnsureSchema();

            // Clear permanent tables (data only — schema stays)
            executeSQLCommand("DELETE FROM ParNames");
            executeSQLCommand("DELETE FROM ParList");
            executeSQLCommand("DELETE FROM SortedParameters");
            executeSQLCommand("DELETE FROM CoefficientWeights");

            // Drop run-specific tables — they are recreated fresh each run by
            // makeCoefficientsTable() and makeResultsTable()
            executeSQLCommand("DROP TABLE IF EXISTS Coefficients");
            executeSQLCommand("DROP TABLE IF EXISTS Results");
            executeSQLCommand("DROP TABLE IF EXISTS INCAInputs");
            executeSQLCommand("DROP TABLE IF EXISTS Observations");
            executeSQLCommand("DROP TABLE IF EXISTS ParameterSensitivitySummary");

            CloseConnection();
        }

        public void processParameterData()
        {
            if (!OpenConnection())
            {
                Console.WriteLine("Could not process parameter data");
                return;
            }

            // vw_sampled_pars replaces the Access saved query [102 Sampled Pars]
            executeSQLCommand(
                "INSERT INTO SortedParameters (ParID, ParameterValue, RunID) " +
                "SELECT p.ParID, p.NumericValue, p.RunID " +
                "FROM   ParList p " +
                "INNER JOIN vw_sampled_pars s ON p.ParID = s.ParID " +
                "ORDER BY p.ParID, p.NumericValue");

            CloseConnection();
        }

        // Legacy version — appears unused in current codebase but retained for reference.
        public void _processParameterData()
        {
            if (!OpenConnection()) return;

            // vw_sampled_pars replaces the Access saved query [102 Sampled Pars]
            using (var cmd = new SQLiteCommand(
                "INSERT INTO SortedParameters (ParID, ParameterValue, RunID) " +
                "SELECT p.ParID, p.NumericValue, p.RunID " +
                "FROM   ParList p " +
                "INNER JOIN vw_sampled_pars s ON p.ParID = s.ParID " +
                "ORDER BY p.ParID, p.NumericValue",
                localConnection))
            {
                try { cmd.ExecuteNonQuery(); }
                catch (Exception ex) { Console.WriteLine(ex.Message); }
            }

            CloseConnection();
        }

        public void createParameterSensitivitySummaryTable()
        {
            if (!OpenConnection()) return;

            // vw_statistics_summary replaces the Access saved query [116 Statistics Summary].
            // CREATE TABLE ... AS SELECT replaces Access's SELECT ... INTO syntax.
            executeSQLCommand(
                "CREATE TABLE ParameterSensitivitySummary AS " +
                "SELECT ParName, ParID, D, MinOfNumericValue, MaxOfNumericValue, xRange, z, p " +
                "FROM   vw_statistics_summary");

            CloseConnection();
        }

        // -------------------------------------------------------------------------
        // Table creation — Results
        // -------------------------------------------------------------------------

        public void makeResultsTable()
        {
            if (!OpenConnection()) return;

            switch (MCParameters.model)
            {
                case 1:  // PERSiST 1.4
                case 8:  // PERSiST 1.6
                case 10: // PERSiST 2.0
                    makePERSiSTResultsTable();
                    makeINCAInputsTable();
                    break;
                case 2:  // INCA-C 1.7
                    makeINCA_CResultsTable();
                    break;
                case 3:  // INCA-PEco
                case 4:  // INCA-P
                case 5:  // INCA-Contaminants
                case 6:  // INCA-Path
                case 11: // INCA-C 2.x
                case 12: // INCA-N Classic
                case 13: // INCA-C 1.8
                    notYetImplemented();
                    break;
                case 7:  // INCA-Hg
                    makeINCA_HgResultsTable();
                    break;
                case 9:  // INCA_ONTHE
                    makeINCA_ONTHEResultsTable();
                    break;
                default:
                    Console.WriteLine("Something has gone wrong when making the RESULTS table");
                    break;
            }

            CloseConnection();
        }

        private void makePERSiSTResultsTable()
        {
            executeSQLCommand(
                "CREATE TABLE Results (" +
                "RUN              INTEGER," +
                "RowNumber         INTEGER," +
                "Reach             TEXT," +
                "TerrestrialInput  REAL," +
                "Flow              REAL," +
                "DateStamp         TEXT)");
        }

        private void makeINCA_CResultsTable()
        {
            executeSQLCommand(
                "CREATE TABLE Results (" +
                "RUN       INTEGER," +
                "RowNumber  INTEGER," +
                "Reach      TEXT," +
                "Flow       REAL," +
                "DateStamp  TEXT)");
        }

        private void makeINCA_HgResultsTable()
        {
            executeSQLCommand(
                "CREATE TABLE Results (" +
                "RUN       INTEGER," +
                "RowNumber  INTEGER," +
                "Reach      TEXT," +
                "Flow       REAL," +
                "DateStamp  TEXT)");
        }

        private void makeINCAInputsTable()
        {
            executeSQLCommand(
                "CREATE TABLE INCAInputs (" +
                "FileName   TEXT," +
                "RUN        INTEGER," +
                "RowNumber   INTEGER," +
                "SMD         REAL," +
                "HER         REAL," +
                "T           REAL," +
                "P           REAL," +
                "DateStamp   TEXT)");
        }

        private void makeINCA_ONTHEResultsTable()
        {
            executeSQLCommand(
                "CREATE TABLE Results (" +
                "FileName       TEXT," +
                "RUN            INTEGER," +
                "RowNumber      INTEGER," +
                "FLOW           REAL," +
                "NITRATE        REAL," +
                "AMMONIUM       REAL," +
                "VOLUME         REAL," +
                "DON            REAL," +
                "VELOCITY       REAL," +
                "WIDTH          REAL," +
                "DEPTH          REAL," +
                "AREA           REAL," +
                "PERIMETER      REAL," +
                "RADIUS         REAL," +
                "RESIDENCETIME  REAL," +
                "DateStamp      TEXT)");
        }

        public void makeObservationsTable()
        {
            if (!OpenConnection()) return;

            // Fixed: original was missing a comma between QC and DateStamp
            executeSQLCommand(
                "CREATE TABLE Observations (" +
                "Reach      TEXT," +
                "Parameter  TEXT," +
                "Value      REAL," +
                "QC         TEXT," +
                "DateStamp  TEXT)");

            CloseConnection();
        }

        // -------------------------------------------------------------------------
        // Table creation — Coefficients
        // -------------------------------------------------------------------------

        public void makeCoefficientsTable()
        {
            if (!OpenConnection()) return;

            switch (MCParameters.model)
            {
                case 1:  // PERSiST 1.4
                case 8:  // PERSiST 1.6
                    makePERSiSTCoefficientsTable();
                    break;
                case 2:  // INCA-C 1.7
                case 11: // INCA-C 2.x
                    makeINCA_CCoefficientsTable();
                    break;
                case 3:  // INCA-PEco
                    makeINCA_PEcoCoefficientsTable();
                    break;
                case 4:  // INCA-P
                    makeINCA_PCoefficientsTable();
                    break;
                case 5:  // INCA-Contaminants
                case 6:  // INCA-Path
                    notYetImplemented();
                    break;
                case 7:  // INCA-Hg
                    makeINCA_HgCoefficientsTable();
                    break;
                case 9:  // INCA_ONTHE
                    makeINCA_ONTHECoefficientsTable();
                    break;
                case 10: // PERSiST 2.0
                    makePERSiST_v2CoefficientsTable();
                    break;
                case 12: // INCA-N Classic
                    makeINCA_NCoefficientsTable();
                    break;
                case 13: // INCA-C 1.8
                    makeINCA_C18CoefficientsTable();
                    break;
                default:
                    Console.WriteLine("Something has gone wrong when making the COEFFICIENTS table");
                    Console.ReadLine();
                    break;
            }

            CloseConnection();
        }

        private void makeDefaultCoefficientsTable()
        {
            executeSQLCommand(
                "CREATE TABLE Coefficients (" +
                "RUN       INTEGER," +
                "RowNumber  INTEGER," +
                "Reach      TEXT," +
                "Parameter  TEXT," +
                "R2         REAL," +
                "NS         REAL," +
                "RMSE       REAL," +
                "RE         REAL," +
                "DateStamp  TEXT)");
        }

        private void makeINCA_NCoefficientsTable()  { makeDefaultCoefficientsTable(); }
        private void makeINCA_CCoefficientsTable()  { makeDefaultCoefficientsTable(); }
        private void makeINCA_HgCoefficientsTable() { makeDefaultCoefficientsTable(); }

        private void makeINCA_ONTHECoefficientsTable()
        {
            executeSQLCommand(
                "CREATE TABLE Coefficients (" +
                "RUN       INTEGER," +
                "RowNumber  INTEGER," +
                "Reach      TEXT," +
                "Parameter  TEXT," +
                "R2         REAL," +
                "NS         REAL," +
                "logNS      REAL," +
                "AD         REAL," +
                "VAR        REAL," +
                "KGE        REAL," +
                "DateStamp  TEXT)");
        }

        private void makeINCA_PEcoCoefficientsTable()
        {
            executeSQLCommand(
                "CREATE TABLE Coefficients (" +
                "RUN       INTEGER," +
                "RowNumber  INTEGER," +
                "Reach      TEXT," +
                "Parameter  TEXT," +
                "R2         REAL," +
                "NS         REAL," +
                "logNS      REAL," +
                "RMSE       REAL," +
                "AD         REAL," +
                "VR         REAL," +
                "KGE        REAL," +
                "CAT_B      REAL," +
                "CAT_C      REAL," +
                "CAT_CA     REAL," +
                "CAT_CB     REAL," +
                "DateStamp  TEXT)");
        }

        private void makeINCA_PCoefficientsTable()
        {
            executeSQLCommand(
                "CREATE TABLE Coefficients (" +
                "RUN       INTEGER," +
                "RowNumber  INTEGER," +
                "Reach      TEXT," +
                "Parameter  TEXT," +
                "R2         REAL," +
                "NS         REAL," +
                "RMSE       REAL," +
                "RE         REAL," +
                "VR         REAL," +
                "DateStamp  TEXT)");
        }

        private void makePERSiSTCoefficientsTable()
        {
            executeSQLCommand(
                "CREATE TABLE Coefficients (" +
                "RUN       INTEGER," +
                "RowNumber  INTEGER," +
                "Reach      TEXT," +
                "R2         REAL," +
                "NS         REAL," +
                "LOG_NS     REAL," +
                "RMSE       REAL," +
                "RE         REAL," +
                "AD         REAL," +
                "VAR        REAL," +
                "N          REAL," +
                "N_RE       REAL," +
                "SS         REAL," +
                "LOG_SS     REAL," +
                "DateStamp  TEXT)");
        }

        private void makeINCA_C18CoefficientsTable()
        {
            executeSQLCommand(
                "CREATE TABLE Coefficients (" +
                "RUN       INTEGER," +
                "RowNumber  INTEGER," +
                "Reach      TEXT," +
                "Parameter  TEXT," +
                "R2         REAL," +
                "NS         REAL," +
                "LOG_NS     REAL," +
                "RMSE       REAL," +
                "RE         REAL," +
                "AD         REAL," +
                "VAR        REAL," +
                "N          REAL," +
                "N_RE       REAL," +
                "DateStamp  TEXT)");
        }

        private void makePERSiST_v2CoefficientsTable()
        {
            executeSQLCommand(
                "CREATE TABLE Coefficients (" +
                "RUN       INTEGER," +
                "RowNumber  INTEGER," +
                "Reach      TEXT," +
                "Parameter  TEXT," +
                "R2         REAL," +
                "NS         REAL," +
                "LOG_NS     REAL," +
                "RMSE       REAL," +
                "RE         REAL," +
                "AD         REAL," +
                "VAR        REAL," +
                "N          REAL," +
                "N_RE       REAL," +
                "SS         REAL," +
                "LOG_SS     REAL," +
                "DateStamp  TEXT)");
        }

        // -------------------------------------------------------------------------
        // Write results and coefficients
        // -------------------------------------------------------------------------

        private void notYetImplemented()
        {
            Console.WriteLine("This feature is not yet implemented for this version of INCA");
            Console.WriteLine("Text files are generated which can be used for subsequent analysis");
        }

        public void writeResults()
        {
            if (!OpenConnection()) return;
            // Result writing is not yet implemented for any model version.
            // Text file output is generated instead by SummarizeResults.write().
            notYetImplemented();
            CloseConnection();
        }

        public void writeCoefficients()
        {
            if (!OpenConnection()) return;

            switch (MCParameters.model)
            {
                case 1:  // PERSiST 1.4
                case 8:  // PERSiST 1.6
                    writePERSiSTCoefficients();
                    break;
                case 2:  // INCA-C 1.7
                case 7:  // INCA-Hg
                    writeGenericINCA_Coefficients();
                    break;
                case 3:  // INCA-PEco
                    writeINCA_PEcoCoefficients();
                    break;
                case 4:  // INCA-P
                    notYetImplemented();
                    break;
                case 5:  // INCA-Contaminants
                case 6:  // INCA-Path
                    notYetImplemented();
                    break;
                case 9:  // INCA_ONTHE
                    writeINCA_ONTHECoefficients();
                    break;
                case 10: // PERSiST 2.0
                    writePERSiST_v2Coefficients();
                    break;
                case 11: // INCA-C 2.x
                    writeGenericINCA_Coefficients();
                    break;
                case 12: // INCA-N Classic
                    writeINCA_NCoefficients();
                    break;
                case 13: // INCA-C 1.8
                    writeINCA_C18Coefficients();
                    break;
                default:
                    Console.WriteLine("Something has gone wrong when populating the COEFFICIENTS table");
                    break;
            }

            CloseConnection();
        }

        private void writeINCA_C18Coefficients()
        {
            using (var transaction = localConnection.BeginTransaction())
            try
            {
                using (StreamReader sr = new StreamReader(MCParameters.coefficientsSummaryFile))
                {
                    string reachName = "undefined";
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        try
                        {
                            string[] fields = line.Split(MCParameters.separatorChar);
                            int rownum = int.Parse(fields[1]);
                            if ((rownum % 7) == 0)
                            {
                                reachName = fields[2];
                            }
                            else if ((rownum % 7) > 1)
                            {
                                InsertCoefficients(
                                    "(RUN, RowNumber, Reach, Parameter, R2, NS, LOG_NS, RMSE, RE, AD, VAR, N, N_RE, DateStamp)",
                                    fields[0], fields[1], reachName, fields[2],
                                    fields[3], fields[4], fields[5], fields[6],
                                    fields[7], fields[8], fields[9], fields[10],
                                    fields[11]);
                            }
                        }
                        catch (Exception ex) { Console.WriteLine(ex.Message); }
                    }
                }
                transaction.Commit();
            }
            catch (Exception ex) { Console.WriteLine(ex.Message); transaction.Rollback(); }
        }

        private void writePERSiSTCoefficients()
        {
            using (var transaction = localConnection.BeginTransaction())
            try
            {
                using (StreamReader sr = new StreamReader(MCParameters.coefficientsSummaryFile))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        try
                        {
                            string[] fields = line.Split(MCParameters.separatorChar);
                            if (fields[1].Equals("0"))
                            {
                                Console.WriteLine(line);
                            }
                            else
                            {
                                InsertCoefficients(
                                    "(RUN, RowNumber, Reach, R2, NS, LOG_NS, RMSE, RE, AD, VAR, N, N_RE, SS, LOG_SS, DateStamp)",
                                    fields[0], fields[1], fields[2],
                                    fields[3], fields[4], fields[5], fields[6],
                                    fields[7], fields[8], fields[9], fields[10],
                                    fields[11], fields[12], fields[13]);
                            }
                        }
                        catch (Exception ex) { Console.WriteLine(ex.Message); }
                    }
                }
                transaction.Commit();
            }
            catch (Exception ex) { Console.WriteLine(ex.Message); transaction.Rollback(); }
        }

        private void writePERSiST_v2Coefficients()
        {
            using (var transaction = localConnection.BeginTransaction())
            try
            {
                using (StreamReader sr = new StreamReader(MCParameters.coefficientsSummaryFile))
                {
                    string line;
                    string reachName = "undefined";
                    while ((line = sr.ReadLine()) != null)
                    {
                        try
                        {
                            string[] fields = line.Split(MCParameters.separatorChar);
                            if (fields.Length > 3)
                            {
                                int rownum = int.Parse(fields[1]);
                                if ((rownum % 7) == 0)
                                {
                                    reachName = fields[2];
                                }
                                else
                                {
                                    InsertCoefficients(
                                        "(RUN, RowNumber, Reach, Parameter, R2, NS, LOG_NS, RMSE, RE, AD, VAR, N, N_RE, SS, LOG_SS, DateStamp)",
                                        fields[0], fields[1], reachName, fields[2],
                                        fields[3], fields[4], fields[5], fields[6],
                                        fields[7], fields[8], fields[9], fields[10],
                                        fields[11], fields[12], fields[13]);
                                }
                            }
                        }
                        catch (Exception ex) { Console.WriteLine(ex.Message); }
                    }
                }
                transaction.Commit();
            }
            catch (Exception ex) { Console.WriteLine(ex.Message); transaction.Rollback(); }
        }

        private void writeGenericINCA_Coefficients()
        {
            using (var transaction = localConnection.BeginTransaction())
            try
            {
                using (StreamReader sr = new StreamReader(MCParameters.coefficientsSummaryFile))
                {
                    string line;
                    string reachName = "undefined";
                    while ((line = sr.ReadLine()) != null)
                    {
                        try
                        {
                            string[] fields = line.Split(MCParameters.separatorChar);
                            if (fields.Length > 2)
                            {
                                int rownum = int.Parse(fields[1]);
                                if ((rownum % 8) == 0)
                                {
                                    reachName = fields[2];
                                }
                                else
                                {
                                    InsertCoefficients(
                                        "(RUN, RowNumber, Reach, Parameter, R2, NS, RMSE, RE, DateStamp)",
                                        fields[0], fields[1], reachName, fields[2],
                                        fields[3], fields[4], fields[5], fields[6]);
                                }
                            }
                        }
                        catch (Exception ex) { Console.WriteLine(ex.Message); }
                    }
                }
                transaction.Commit();
            }
            catch (Exception ex) { Console.WriteLine(ex.Message); transaction.Rollback(); }
        }

        private void writeINCA_NCoefficients()
        {
            using (var transaction = localConnection.BeginTransaction())
            try
            {
                using (StreamReader sr = new StreamReader(MCParameters.coefficientsSummaryFile))
                {
                    string line;
                    string reachName = "undefined";
                    while ((line = sr.ReadLine()) != null)
                    {
                        try
                        {
                            string[] fields = line.Split(MCParameters.separatorChar);
                            if (fields.Length > 2)
                            {
                                int rownum = int.Parse(fields[1]);
                                if ((rownum % 6) == 0)
                                {
                                    reachName = fields[2];
                                }
                                else
                                {
                                    InsertCoefficients(
                                        "(RUN, RowNumber, Reach, Parameter, R2, NS, RMSE, RE, DateStamp)",
                                        fields[0], fields[1], reachName + "_Reach", fields[2],
                                        fields[3], fields[4], fields[5], fields[6]);
                                }
                            }
                        }
                        catch (Exception ex) { Console.WriteLine(ex.Message); }
                    }
                }
                transaction.Commit();
            }
            catch (Exception ex) { Console.WriteLine(ex.Message); transaction.Rollback(); }
        }

        private void writeINCA_ONTHECoefficients()
        {
            using (var transaction = localConnection.BeginTransaction())
            try
            {
                using (StreamReader sr = new StreamReader(MCParameters.coefficientsSummaryFile))
                {
                    string line;
                    string reachName = "";
                    while ((line = sr.ReadLine()) != null)
                    {
                        string[] fields = line.Split(MCParameters.separatorChar);
                        if (fields.Length == 3)
                        {
                            Console.WriteLine(line);
                            reachName = fields[2];
                        }
                        else if (fields.Length > 4)
                        {
                            InsertCoefficients(
                                "(Run, RowNumber, Reach, Parameter, R2, NS, logNS, AD, VAR, KGE, DateStamp)",
                                fields[0], fields[1], reachName, fields[2],
                                fields[3], fields[4], fields[5],
                                fields[7], fields[8], fields[13]);
                        }
                    }
                }
                transaction.Commit();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                transaction.Rollback();
            }
        }

        private void writeINCA_PEcoCoefficients()
        {
            using (var transaction = localConnection.BeginTransaction())
            try
            {
                using (StreamReader sr = new StreamReader(MCParameters.coefficientsSummaryFile))
                {
                    string line;
                    string reachName = "";
                    while ((line = sr.ReadLine()) != null)
                    {
                        string[] fields = line.Split(MCParameters.separatorChar);
                        int fieldNumber;
                        if (int.TryParse(fields[1], out fieldNumber) && (fieldNumber % 16) == 0)
                        {
                            Console.WriteLine(line);
                            reachName = fields[2];
                        }
                        if (fields.Length > 10)
                        {
                            InsertCoefficients(
                                "(Run, RowNumber, Reach, Parameter, R2, NS, logNS, RMSE, AD, VR, KGE, CAT_B, CAT_C, Cat_Ca, Cat_Cb, DateStamp)",
                                fields[0], fields[1], reachName, fields[2],
                                fields[3], fields[4], fields[5], fields[6],
                                fields[8], fields[9], fields[13],
                                fields[14], fields[15], fields[16], fields[17]);
                        }
                    }
                }
                transaction.Commit();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                transaction.Rollback();
            }
        }

        // -------------------------------------------------------------------------
        // Write parameter sets and names
        // -------------------------------------------------------------------------

        public void writeParameterSet(int runID, parameterSet pSet)
        {
            Console.WriteLine("Writing parameter set {0}", runID);
            if (!OpenConnection()) return;

            using (var transaction = localConnection.BeginTransaction())
            try
            {
                int m = 0;
                foreach (ArrayList l in pSet)
                {
                    foreach (parameter p in l)
                    {
                        string[] s = (p.stringValue()).Split(MCParameters.separatorChar);
                        foreach (string par in s)
                            writeParameter(runID, m++, par);
                    }
                }
                transaction.Commit();
            }
            catch (Exception ex) { Console.WriteLine(ex.Message); transaction.Rollback(); }

            CloseConnection();
        }

        public void writeParameterNames(ParameterArrayList pal)
        {
            if (!OpenConnection()) return;

            using (var transaction = localConnection.BeginTransaction())
            try
            {
                string[] s = (pal.header.ToString()).Split('\n');
                int m = 0;
                foreach (string par in s)
                {
                    int splitPos = par.IndexOf(MCParameters.separatorChar);
                    if (splitPos >= 0)
                    {
                        string parName = par.Substring(splitPos + 1).Trim();
                        writeParameterName(m++, parName);
                    }
                }
                transaction.Commit();
            }
            catch (Exception ex) { Console.WriteLine(ex.Message); transaction.Rollback(); }

            CloseConnection();
        }

        public void writeCoefficientWeights()
        {
            if (!OpenConnection()) return;

            using (var transaction = localConnection.BeginTransaction())
            try
            {
                using (StreamReader coefficientWeights = new StreamReader(MCParameters.coefficientsWeightFile))
                {
                    string line;
                    while ((line = coefficientWeights.ReadLine()) != null)
                    {
                        string[] fields = line.Split(MCParameters.separatorChar);
                        using (var cmd = new SQLiteCommand(
                            "INSERT INTO CoefficientWeights (CoefficientName, CoefficientWeight) " +
                            "VALUES (@name, @weight)",
                            localConnection))
                        {
                            cmd.Parameters.AddWithValue("@name",   fields[0].Trim());
                            cmd.Parameters.AddWithValue("@weight", fields[1].Trim());
                            try { cmd.ExecuteNonQuery(); }
                            catch (Exception ex) { Console.WriteLine(ex.Message); }
                        }
                    }
                }
                transaction.Commit();
            }
            catch (Exception ex) { Console.WriteLine(ex.Message); transaction.Rollback(); }

            CloseConnection();
        }

        // -------------------------------------------------------------------------
        // Private insert helpers
        // -------------------------------------------------------------------------

        private void writeParameter(int runID, int parID, string textValue)
        {
            double numericValue;
            bool isNumeric = double.TryParse(textValue,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out numericValue);

            using (var cmd = new SQLiteCommand(
                "INSERT INTO ParList (RunID, ParID, TextValue, NumericValue) " +
                "VALUES (@runID, @parID, @text, @numeric)",
                localConnection))
            {
                cmd.Parameters.AddWithValue("@runID",   runID);
                cmd.Parameters.AddWithValue("@parID",   parID);
                cmd.Parameters.AddWithValue("@text",    textValue);
                cmd.Parameters.AddWithValue("@numeric", isNumeric ? (object)numericValue : DBNull.Value);
                try { cmd.ExecuteNonQuery(); }
                catch (SQLiteException ex) { Console.WriteLine(ex.Message); }
            }
        }

        private void writeParameterName(int parID, string parName)
        {
            using (var cmd = new SQLiteCommand(
                "INSERT INTO ParNames (ParID, ParName) VALUES (@parID, @parName)",
                localConnection))
            {
                cmd.Parameters.AddWithValue("@parID",   parID);
                cmd.Parameters.AddWithValue("@parName", parName);
                try { cmd.ExecuteNonQuery(); }
                catch (SQLiteException ex) { Console.WriteLine(ex.Message); }
            }
        }

        /// <summary>
        /// Convenience helper for the coefficient write methods.
        /// Builds and executes an INSERT INTO Coefficients statement, appending
        /// date('now') as the final value automatically.
        /// </summary>
        private void InsertCoefficients(string columnList, params string[] values)
        {
            // Build a VALUES clause with one placeholder per supplied value plus date('now')
            string placeholders = string.Join(", ", values.Select((_, i) => $"@v{i}"));
            string sql = $"INSERT INTO Coefficients {columnList} VALUES ({placeholders}, date('now'))";

            using (var cmd = new SQLiteCommand(sql, localConnection))
            {
                for (int i = 0; i < values.Length; i++)
                    cmd.Parameters.AddWithValue($"@v{i}", values[i].Trim());

                try { cmd.ExecuteNonQuery(); }
                catch (SQLiteException ex) { Console.WriteLine(ex.Message); }
            }
        }

        private void executeSQLCommand(string commandString)
        {
            using (var cmd = new SQLiteCommand(commandString, localConnection))
            {
                try { cmd.ExecuteNonQuery(); }
                catch (SQLiteException ex) { Console.WriteLine(ex.Message); }
            }
        }

        // -------------------------------------------------------------------------
        // Legacy / slow result-writing methods (retained from original, not called)
        // -------------------------------------------------------------------------

        private void writePERSiSTResultsToDatabase()
        {
            // Original comment: "this is really slow, needs to be refactored"
            for (var i = 0; i < MCParameters.splitsToUse; i++)
            {
                string fileName = MCParameters.resultFileNameStub + i.ToString() + ".txt";
                using (StreamReader sr = new StreamReader(fileName))
                {
                    string line;
                    string reach = "";
                    while ((line = sr.ReadLine()) != null)
                    {
                        string[] fields = line.Split(MCParameters.separatorChar);
                        if (fields.Length == 3)
                        {
                            reach = fields[2];
                            Console.WriteLine("Processing results for iteration " + fields[0] + ", reach " + reach);
                        }
                        else
                        {
                            using (var cmd = new SQLiteCommand(
                                "INSERT INTO Results (RUN, RowNumber, Reach, TerrestrialInput, Flow, DateStamp) " +
                                "VALUES (@run, @row, @reach, @terr, @flow, date('now'))",
                                localConnection))
                            {
                                cmd.Parameters.AddWithValue("@run",   fields[0]);
                                cmd.Parameters.AddWithValue("@row",   fields[1]);
                                cmd.Parameters.AddWithValue("@reach", reach);
                                cmd.Parameters.AddWithValue("@terr",  fields[2]);
                                cmd.Parameters.AddWithValue("@flow",  fields[3]);
                                try { cmd.ExecuteNonQuery(); }
                                catch (Exception ex) { Console.WriteLine(ex.Message); }
                            }
                        }
                    }
                }
            }
        }

        private void writePERSiSTResults()
        {
            string PERSiSTOutputFile = "PERSiSTResults.txt";
            File.Create(PERSiSTOutputFile).Dispose();

            for (var i = 0; i < MCParameters.splitsToUse; i++)
            {
                string fileName = MCParameters.resultFileNameStub + i.ToString() + ".txt";
                using (StreamReader sr = new StreamReader(fileName))
                {
                    string line;
                    string reach = "";
                    while ((line = sr.ReadLine()) != null)
                    {
                        string[] fields = line.Split(MCParameters.separatorChar);
                        if (fields.Length == 3)
                        {
                            reach = fields[2];
                            Console.WriteLine("Processing results for iteration " + fields[0] + ", reach " + reach);
                        }
                        else
                        {
                            string resultString =
                                fields[0] + ", " +
                                fields[1] + ", '" +
                                reach + "', " +
                                fields[2] + ", " +
                                fields[3];
                            double tst;
                            try
                            {
                                tst = Convert.ToDouble(fields[2]);
                                using (FileStream fs = new FileStream(PERSiSTOutputFile, FileMode.Append, FileAccess.Write))
                                using (StreamWriter sw = new StreamWriter(fs))
                                { sw.WriteLine(resultString); }
                            }
                            catch { }
                        }
                    }
                }
            }
        }

        private void writeINCAResultsFromPERSiST()
        {
            string INCASummaryFile = "INCASummary.txt";
            File.Create(INCASummaryFile).Dispose();

            string[] resultFiles = Directory.GetFiles(
                Directory.GetCurrentDirectory(),
                MCParameters.INCAFileNameStub + "*.txt");

            foreach (string f in resultFiles)
            {
                using (StreamReader sr = new StreamReader(f))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        string[] fields = line.Split('\t');
                        try
                        {
                            string resultString =
                                f + ", " + fields[0] + "," + fields[1] + "," +
                                fields[2] + "," + fields[3] + "," +
                                fields[4] + "," + fields[5];

                            using (FileStream fs = new FileStream(INCASummaryFile, FileMode.Append, FileAccess.Write))
                            using (StreamWriter sw = new StreamWriter(fs))
                            { sw.WriteLine(resultString); }
                        }
                        catch (Exception ex) { Console.WriteLine(ex.Message); }
                    }
                }
            }
        }
    }
}
