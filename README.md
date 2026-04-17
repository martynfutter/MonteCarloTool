# GenericMC — Monte Carlo Calibration Tool for INCA/PERSiST Models

GenericMC is a C# (.NET) command-line tool for automated parameter estimation and uncertainty analysis of catchment-scale water quality models. It supports two complementary sampling strategies — Markov Chain Monte Carlo (MCMC) and Generalised Likelihood Uncertainty Estimation (GLUE) — and interfaces with a family of INCA and PERSiST model executables.

---

## Supported Models

| ID | Model | Version |
|----|-------|---------|
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
- **Results database** — outputs written to a Microsoft Access database (`mc.accdb`) for post-processing
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
├── Class_resultsDatabase.cs       # MS Access database interaction (ODBC)
├── Class_GLUEAsASideEffect.cs     # GLUE run accounting and parameter saving
├── Class_NumericalSupport.cs      # Normal CDF inverse (for MCMC jump proposals)
├── Class_TestBed.cs               # Developer test harness
└── performanceStatistic.cs        # Performance statistic class hierarchy
```

---

## Requirements

- **Runtime:** .NET Framework 4.8 (Windows)
- **Database:** Microsoft Access runtime (for `.accdb` output); ODBC driver required
- **Model executables:** one or more of the supported INCA/PERSiST command-line executables must be present in the working directory

---

## Getting Started

### Build

Open `MCDemo.csproj` in Visual Studio (2013 or later) and build in Release or Debug mode. The output executable is `GenericMC.exe`.

### Required Files

Before running, place the following in the working directory alongside the executable:

| File | Description |
|------|-------------|
| `mc.par` | Initial parameter file (or model-specific name) |
| `<model>_min.par` | Lower bounds for each parameter |
| `<model>_max.par` | Upper bounds for each parameter |
| `mc.accdb` | Blank Access database with the expected table schema |
| Model executable | e.g. `persist_cmd.exe`, `inca_c_cmd.exe` |
| Data/obs files | As required by the chosen model |

The minimum/maximum parameter files follow the same whitespace-delimited format as the model's own `.par` files. Non-numeric tokens (strings) are carried through unchanged and are not perturbed during sampling.

### Running

```
GenericMC.exe
```

The tool is fully interactive. On startup it will ask you to:

1. Choose **MCTool** (MCMC) or **GLUE**
2. Select the model
3. Enter the number of ensemble members and MCMC jump iterations
4. Provide all required file names (parameter, data, observed, etc.)
5. Set per-statistic and per-series weights
6. Set the jump scaling factor

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

Runs a fixed number (default 12,500) of randomly sampled parameter sets using LHS, saves each set and its likelihood score to disk for post-processing.

### Performance Statistic

The composite objective function is a weighted sum over all observed series:

```
F = Σ_i  w_series[i] × Σ_j  w_coeff[j] × f_j(series i)
```

where `f_j` transforms each raw statistic into a value that should be **maximised** (e.g. NS → NS − 1, RMSE → −RMSE, AD → −|AD|). When weights for R², AD, and VR are all set to 1 and NS/logNS to 0, the tool automatically computes KGE instead of the weighted sum.

---

## Output Files

| File | Contents |
|------|----------|
| `bestParSet<N>.par` | Best parameter set for ensemble member N |
| `logBestPerformance.txt` | Performance index log across MCMC iterations |
| `mc.accdb` | Full results database (parameters, coefficients) |
| `pars.csv` | All parameter sets as a flat CSV array |
| `parNames.csv` | Parameter name list |
| `parList.csv` | Parameter values list |
| `coefficients.txt` | Concatenated goodness-of-fit statistics |
| `results<N>.txt` | Model output for split N |
| `GLUEPerformance.csv` | GLUE run scores (GLUE mode only) |
| `SuccessfulCompletion.txt` | Written on clean exit |

---

## Configuration Reference

Key constants in `Class_MCParameters.cs`:

| Parameter | Default | Description |
|-----------|---------|-------------|
| `maxTries` | 300 | Number of ensemble members to find |
| `maxJumps` | 2500 | MCMC proposals per ensemble member |
| `maxUnsuccessfulJumps` | 50 | Consecutive failures before restart |
| `defaultScalingFactorForJump` | 0.01 | Jump size relative to parameter range |
| `testPerformanceAdjustmentFactor` | 1 | Exponent applied to the acceptance test draw |
| `splitsToUse` | 20 | Number of output result files |
| `outputSize` | `"medium"` | Passed to model via `-size` flag |

---

## Notes and Limitations

- The Access database backend requires a 32-bit ODBC driver; the project is built for x86.
- Several `writeResults()` branches are marked `notYetImplemented()` and produce text files instead of database records.
- `Class_interactWithDatabasecs.cs` contains an earlier, partial database class that is superseded by `Class_resultsDatabase.cs`.
- `tmp.cs` is an incomplete code fragment included in the repository and is not compiled.
- The GLUE iteration count (12,500) is currently hard-coded in `Program.cs`.

---

## License

No license file is present in the repository. Copyright © 2012 as noted in `AssemblyInfo.cs`.
