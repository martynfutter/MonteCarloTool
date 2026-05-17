# GenericMC — Monte Carlo Calibration Tool for INCA/PERSiST Models

GenericMC is a C# (.NET) command-line tool for automated parameter estimation and uncertainty analysis of catchment-scale water quality models. It supports two complementary sampling strategies — Markov Chain Monte Carlo (MCMC) and Generalised Likelihood Uncertainty Estimation (GLUE) — and interfaces with a family of INCA and PERSiST model executables.

---

## Supported Models

| ID | Model | Version |
| --- | --- | --- |
| 1 | PERSiST | 1.4.x |
| 2 | INCA-C | 1.7 |
| 3 | INCA-PEco | All |
| 4 | INCA-P | 1.4.x |
| 5 | INCA-Contaminants (incl. Microplastics) | All |
| 6 | INCA-Path | 1.0 |
| 7 | INCA-Hg | 1.4 |
| 8 | PERSiST | 1.6.x |
| 9 | INCA(ON)THE | 1.0 |
| 10 | PERSiST | 2.0 |
| 11 | INCA-C | 2.0 |
| 12 | INCA-N | 1.0 |
| 13 | INCA-C | 1.8 |

---

## Features

- **MCMC calibration** — Metropolis-Hastings reversible-jump sampler with adaptive jump sizes and automatic restart from poor-performing regions
- **GLUE analysis** — Latin Hypercube Sampling (LHS) for broad uncertainty quantification
- **Multi-objective performance statistics** — weighted combinations of Nash-Sutcliffe (NS), log(NS), Pearson R², RMSE, Absolute Difference (AD), Variance Ratio (VR), Kling-Gupta Efficiency (KGE), and Limits of Acceptability (CatB/C)
- **Per-series weighting** — individual weights for each observed data series and each statistic
- **Results database** — outputs written to a SQLite database (`mc.db`) for post-processing; no proprietary database runtime required
- **KS sensitivity analysis** — Kolmogorov-Smirnov parameter sensitivity chain implemented as SQLite views, queryable interactively via [DB Browser for SQLite](https://sqlitebrowser.org/)
- **Parameter array output** — full parameter sets saved to CSV for external analysis

---

## Project Structure

```
MCDemo/
├── Program.cs                     # Entry point; routes to MC or GLUE workflow
├── Class_MC.cs                    # Simple standalone MC sampler (legacy/demo)
├── Class_MCMCController.cs        # Main MCMC engine (MCNew)
├── Class_MCParameters.cs          # Global configuration and constants
├── Class_MCRuntimeParameters.cs   # Interactive CLI parameter collection
├── Class_Parameter.cs             # Parameter base class, parameterDouble, parameterString
├── Class_ParameterSet.cs          # Collection of parameters; read/write .par files
├── Class_ParameterArrayList.cs    # Named parameter lists (one per model)
├── Class_ParameterArrayHeader.cs  # CSV header builders (one per model)
├── Class_LHSController.cs         # Latin Hypercube Sampling controller
├── Class_Permutations.cs          # Fisher-Yates shuffle
├── Class_InteractWithModel.cs     # Model execution and performance statistic evaluation
├── Class_CommandString.cs         # Model-specific command-line string builder
├── Class_SummarizeResults.cs      # Post-run result collection and database writing
├── Class_resultsDatabase.cs       # SQLite database interaction; creates schema and KS views
├── Class_PostProcessing.cs        # Post-run KS sensitivity analysis against mc.db
├── Class_GLUEAsASideEffect.cs     # GLUE run accounting and parameter saving
├── Class_NumericalSupport.cs      # Normal CDF inverse (for MCMC jump proposals)
├── Class_TestBed.cs               # Developer test harness
└── performanceStatistic.cs        # Performance statistic class hierarchy
```

---

## Requirements

- **Runtime:** .NET Framework 4.8 (Windows)
- **Database:** SQLite — the database file (`mc.db`) is created automatically in the working directory; no additional runtime or driver installation is required
- **NuGet packages:** `System.Data.SQLite` (v1.0.117) and `EntityFramework` (v6.4.4) — restore automatically via NuGet Package Restore before building
- **Model executables:** one or more of the supported INCA/PERSiST command-line executables must be present in the working directory

---

## Getting Started

### Build

Open `MCDemo.csproj` in Visual Studio (2013 or later) and build in Release or Debug mode. The output executable is `GenericMC.exe`.

### Required Files

Before running, place the following in the working directory alongside the executable:

| File | Description |
| --- | --- |
| `mc.par` | Initial parameter file (or model-specific name) |
| `<model>_min.par` | Lower bounds for each parameter |
| `<model>_max.par` | Upper bounds for each parameter |
| Model executable | e.g. `persist_cmd.exe`, `inca_c_cmd.exe` |
| Data/obs files | As required by the chosen model |

The SQLite database file (`mc.db`) is created automatically on first run. The minimum/maximum parameter files follow the same whitespace-delimited format as the model's own `.par` files. Non-numeric tokens (strings) are carried through unchanged and are not perturbed during sampling.

### Running the Executable

`GenericMC.exe` is a Windows command-line application. The simplest way to run it is:

1. Open a **Command Prompt** (`cmd.exe`) or **PowerShell** window.
2. Navigate to the folder containing the executable and all required input files:
   ```
   cd C:\path\to\your\working\directory
   ```
3. Launch the tool:
   ```
   GenericMC.exe
   ```

Alternatively, you can double-click `GenericMC.exe` in File Explorer — a console window will open automatically.

The tool is fully interactive. On startup it will prompt you to answer a series of questions:

1. Choose **1** for MCTool (MCMC) or **2** for GLUE
2. Select the model number from the Supported Models table above
3. Enter the number of ensemble members and MCMC jump iterations
4. Provide all required file names (parameter, data, observed, etc.) — press Enter to accept any default shown in square brackets
5. Set per-statistic and per-series weights (enter `0` to skip a statistic)
6. Set the jump scaling factor (default `0.01` is a reasonable starting point)

The tool will then run without further interaction. Progress is printed to the console as each ensemble member is found. A `SuccessfulCompletion.txt` file is written in the working directory when the run finishes cleanly.

---

## How It Works

### MCMC Workflow (`MCNew`)

1. Run the model once from the initial parameter file to establish a baseline performance.
2. Randomise parameter values until a starting point is found that is reasonably close to the best known performance.
3. For each requested ensemble member:
   - Propose MCMC jumps drawn from a truncated normal distribution scaled by a user-specified factor.
   - Accept or reject proposals using a Metropolis criterion.
   - If too many consecutive proposals fail, re-randomise and restart the local search.
   - Save the best-performing parameter set for this member to `bestParSet<N>.par`.
4. After all members are found, re-run each saved parameter set at full output resolution and collect model coefficients and results.

### GLUE Workflow

Runs a user-specified number of randomly sampled parameter sets using LHS, saves each set and its likelihood score to disk for post-processing.

### Post-processing and KS Sensitivity Analysis

After all ensemble members have been collected, `Class_PostProcessing` runs a Kolmogorov-Smirnov (KS) parameter sensitivity analysis directly against `mc.db`. The analysis identifies which parameters were actually varied across Monte Carlo runs and tests whether the distribution of each parameter differs between behavioural and non-behavioural model runs.

The analysis is implemented as a chain of SQLite views created automatically inside `mc.db`:

| View | Description |
| --- | --- |
| `vw_par_stats` | Aggregates min, average, and max value for each parameter across all Monte Carlo runs |
| `vw_sampled_pars` | Filters to parameters that were actually varied during the run (min value ≠ max value) |
| `vw_parameter_ranges` | Computes the full value range and run count for each sampled parameter |
| `vw_parameters_with_offsets` | Assigns a rank offset to each parameter value within its range, used to build the empirical CDF |
| `vw_observed_theoretical` | Calculates the observed (empirical) and theoretical (uniform) CDF values at each ranked point |
| `vw_test_statistic` | Computes the absolute difference between the empirical and theoretical CDFs at every point |
| `vw_ks_d_statistic` | Extracts the maximum CDF difference (the KS D statistic) for each parameter |
| `vw_ks_d_with_range` | Joins the D statistic back to the CDF point where it occurs and computes the run-count term used in the z score |
| `vw_ks_d_and_z` | Converts the D statistic to a z score using the standard KS approximation formula |
| `vw_ks_p` | Approximates the two-tailed p-value from z using a 4-term KS series expansion |
| `vw_ks_with_names` | Joins the KS results with parameter names from `ParNames` for readability |
| `vw_statistics_summary` | Final sensitivity table combining parameter names, D statistic, z score, p-value, and sampled value ranges |

The final sensitivity results are also written to the `ParameterSensitivitySummary` table in `mc.db`.

### Performance Statistic

The composite objective function is a weighted sum over all observed series:

```
F = Σ_i  w_series[i] × Σ_j  w_coeff[j] × f_j(series i)
```

where `f_j` transforms each raw statistic into a value that should be **maximised** (e.g. NS → NS − 1, RMSE → −RMSE, AD → −|AD|). When weights for R², AD, and VR are all set to 1 and NS/logNS to 0, the tool automatically computes KGE instead of the weighted sum.

---

## Output Files

| File | Contents |
| --- | --- |
| `bestParSet<N>.par` | Best parameter set for ensemble member N |
| `logBestPerformance.txt` | Performance index log across MCMC iterations |
| `mc.db` | Full results database (parameters, coefficients, KS views) — SQLite format |
| `pars.csv` | All parameter sets as a flat CSV array |
| `parNames.csv` | Parameter name list |
| `parList.csv` | Parameter values list |
| `coefficients.txt` | Concatenated goodness-of-fit statistics |
| `results<N>.txt` | Model output for split N |
| `GLUEPerformance.csv` | GLUE run scores (GLUE mode only) |
| `SuccessfulCompletion.txt` | Written on clean exit |

---

## Exploring the Results Database

All results are stored in `mc.db`, a standard SQLite database file that can be opened with any SQLite-compatible tool. [DB Browser for SQLite](https://sqlitebrowser.org/) is recommended — it is free, open-source, and requires no installation beyond the installer.

### Using DB Browser for SQLite

1. **Download and install** DB Browser from [https://sqlitebrowser.org/dl/](https://sqlitebrowser.org/dl/) (choose the Windows installer).
2. **Open the database:** click **Open Database** and navigate to the `mc.db` file in your working directory.
3. **Browse tables:** the **Database Structure** tab lists all tables and views. Key tables include:
   - `ParNames` — parameter names
   - `ParList` — all parameter values for every run
   - `SortedParameters` — varied parameters sorted for KS analysis
   - `Coefficients` — goodness-of-fit statistics per run
   - `ParameterSensitivitySummary` — final KS sensitivity results
4. **Query views:** switch to the **Execute SQL** tab to run queries directly against the KS analysis views. For example, to see all varied parameters ranked by sensitivity:
   ```sql
   SELECT * FROM vw_statistics_summary ORDER BY D DESC;
   ```
5. **Export results:** right-click any table or query result and choose **Export to CSV** to save data for further analysis in Excel, R, or Python.

### Alternative SQLite Tools

Any tool that supports SQLite 3 can read `mc.db`. Some popular options are:

- **Python** (`sqlite3` standard library or `pandas.read_sql`) — straightforward for scripted post-processing
- **R** (`RSQLite` package) — integrates directly with data frames
- **DBeaver** (free, cross-platform) — full-featured database browser with SQL editor
- **SQLiteOnline** ([sqliteonline.com](https://sqliteonline.com)) — browser-based, no installation required; upload `mc.db` and query directly

---

## Configuration Reference

Key constants in `Class_MCParameters.cs`:

| Parameter | Default | Description |
| --- | --- | --- |
| `maxTries` | 300 | Number of ensemble members to find |
| `maxJumps` | 2500 | MCMC proposals per ensemble member |
| `maxUnsuccessfulJumps` | 50 | Consecutive failures before restart |
| `defaultScalingFactorForJump` | 0.01 | Jump size relative to parameter range |
| `testPerformanceAdjustmentFactor` | 1 | Exponent applied to the acceptance test draw |
| `splitsToUse` | 20 | Number of output result files |
| `outputSize` | `"medium"` | Passed to model via `-size` flag |

---

## Notes and Limitations

- The SQLite database (`mc.db`) is created automatically in the working directory; no pre-existing blank database file is required.
- Several `writeResults()` branches are marked `notYetImplemented()` and produce text files instead of database records.

---

## Acknowledgements

This work was part-funded by the **FORMAS / JPI Water FESTiVAL project**. The authors gratefully acknowledge this support.

---

## License

This project is licensed under the MIT License — see the [LICENSE](LICENSE) file for details.

Copyright © 2012–2026 Martyn Futter.
