using System;
using System.Data;
using System.Data.SQLite;
using System.IO;

namespace MC
{
    /// <summary>
    /// Post-processing operations applied to the SQLite results database
    /// immediately before the program exits.
    ///
    /// Usage — add one line at the end of both MC_Main() and GLUE_Main()
    /// in Program.cs, just before the SuccessfulCompletion block:
    ///
    ///     new PostProcessing().Run();
    ///
    /// Additional post-processing queries can be added as private methods
    /// called from Run().
    /// </summary>
    class PostProcessing
    {
        // -------------------------------------------------------------------------
        // Infrastructure
        // -------------------------------------------------------------------------

        private SQLiteConnection localConnection;

        public PostProcessing()
        {
            localConnection = new SQLiteConnection();
        }

        private bool OpenConnection()
        {
            string dbPath = Path.Combine(Directory.GetCurrentDirectory(), "mc.db");
            localConnection.ConnectionString = "Data Source=" + dbPath + ";Version=3;";
            try
            {
                localConnection.Open();
            }
            catch (SQLiteException ex)
            {
                Console.WriteLine("PostProcessing: could not open database — {0}", ex.Message);
            }
            return localConnection.State == ConnectionState.Open;
        }

        private void CloseConnection()
        {
            if (localConnection != null && localConnection.State == ConnectionState.Open)
                localConnection.Close();
        }

        private void ExecuteSQLCommand(string commandString)
        {
            using (SQLiteCommand cmd = new SQLiteCommand(commandString, localConnection))
            {
                try   { cmd.ExecuteNonQuery(); }
                catch (SQLiteException ex) { Console.WriteLine(ex.Message); }
            }
        }

        // -------------------------------------------------------------------------
        // Post-processing steps
        // -------------------------------------------------------------------------

        /// <summary>
        /// Query 103 — Append Sampled Parameters (SQLite version).
        ///
        /// Populates SortedParameters with every ParList row whose parameter was
        /// actually varied during the run (i.e. min value != max value across all
        /// runs), sorted by ParID then ParameterValue.
        ///
        /// In the original Access database this query joins against two saved
        /// queries:
        ///   "101 Par Stats"   — per-parameter MIN / AVG / MAX over all runs
        ///   "102 Sampled Pars"— filters 101 to rows where min != max
        ///
        /// Because SQLite does not store named queries those two views are
        /// expanded inline here as subqueries so the behaviour is identical.
        ///
        /// Equivalent Access SQL (query "103 Append SampledPars"):
        ///   INSERT INTO SortedParameters (ParID, ParameterValue, RunID)
        ///   SELECT ParList.ParID, ParList.NumericValue, ParList.RunID
        ///   FROM ParList
        ///     INNER JOIN [102 Sampled Pars]
        ///       ON ParList.ParID = [102 Sampled Pars].ParID
        ///   ORDER BY ParList.ParID, ParList.NumericValue;
        /// </summary>
        private void AppendSampledParameters()
        {
            Console.WriteLine("PostProcessing: clearing SortedParameters...");
            ExecuteSQLCommand("DELETE FROM SortedParameters");

            Console.WriteLine("PostProcessing: populating SortedParameters...");

            // Inline expansion of [101 Par Stats] and [102 Sampled Pars]:
            //
            //   Inner subquery  — "101 Par Stats":
            //     groups ParList by ParID, computing MIN and MAX NumericValue.
            //
            //   Outer subquery  — "102 Sampled Pars":
            //     keeps only those ParIDs where MIN != MAX (i.e. the parameter
            //     was varied across Monte Carlo runs).
            //
            //   Outer SELECT    — "103 Append SampledPars":
            //     joins the full ParList against the sampled-parameter set and
            //     inserts the matching rows, ordered for later rank-based work.

            string sql =
                "INSERT INTO SortedParameters (ParID, ParameterValue, RunID) " +
                "SELECT pl.ParID, " +
                "       pl.NumericValue, " +
                "       pl.RunID " +
                "FROM   ParList AS pl " +
                "INNER JOIN ( " +
                "    SELECT ParID " +                              // [102 Sampled Pars]
                "    FROM ( " +
                "        SELECT ParID, " +                        // [101 Par Stats]
                "               MIN(NumericValue) AS MinOfNumericValue, " +
                "               MAX(NumericValue) AS MaxOfNumericValue " +
                "        FROM   ParList " +
                "        GROUP  BY ParID " +
                "    ) AS ParStats " +
                "    WHERE ParStats.MinOfNumericValue != ParStats.MaxOfNumericValue " +
                ") AS SampledPars ON pl.ParID = SampledPars.ParID " +
                "ORDER BY pl.ParID, pl.NumericValue;";

            ExecuteSQLCommand(sql);
            Console.WriteLine("PostProcessing: SortedParameters populated.");
        }

        // -------------------------------------------------------------------------
        // Public entry point
        // -------------------------------------------------------------------------

        /// <summary>
        /// Runs all post-processing steps in sequence.
        /// Call this from Program.cs immediately before the program exits.
        /// </summary>
        public void Run()
        {
            Console.WriteLine("PostProcessing: starting...");
            if (!OpenConnection())
            {
                Console.WriteLine("PostProcessing: aborted — could not connect to database.");
                return;
            }

            AppendSampledParameters();
            // Additional post-processing methods can be called here, e.g.:
            // AppendDValues();
            // ComputeKolmogorovSmirnovStatistics();

            CloseConnection();
            Console.WriteLine("PostProcessing: complete.");
        }
    }
}
