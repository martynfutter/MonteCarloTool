using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;


namespace MC
{
    class Program
    {
        static void Main()
        {
            string s;
            int i;
            Console.WriteLine("******************************");
            Console.WriteLine("* Choose 1 for MCTool        *");
            Console.WriteLine("* Choose 2 for GLUE          *");
            Console.WriteLine("* Any other key to exit      *");
            Console.WriteLine("******************************");
            s = Console.ReadLine();
            int.TryParse(s, out i);

            MCParameters.GLUE = i;

            // Prompt for an optional run subdirectory.
            // All model inputs, executables and outputs will be read from and
            // written to this directory.  Pressing Enter skips the change and
            // preserves the original flat-directory behaviour.
            if (i == 1 || i == 2)
            {
                Console.Write("Please enter the run subdirectory name (press Enter to use current directory): ");
                string subDir = Console.ReadLine().Trim();

                if (!string.IsNullOrEmpty(subDir))
                {
                    if (!Directory.Exists(subDir))
                    {
                        Directory.CreateDirectory(subDir);
                        Console.WriteLine("Created subdirectory: {0}", subDir);
                    }

                    // Redirect all subsequent relative-path file I/O into the
                    // chosen subdirectory.  The spawned model process inherits
                    // this working directory automatically via ProcessStartInfo.
                    Directory.SetCurrentDirectory(subDir);
                    MCParameters.runDirectory = subDir;
                    Console.WriteLine("Working directory set to: {0}", Directory.GetCurrentDirectory());
                }
            }

            switch (i)
            {
                case 1:
                    MC_Main();
                    break;
                case 2:
                    GLUE_Main();
                    break;
                default:
                    Console.WriteLine("No valid argument provided, exiting");
                    break;
            }
        }

        static void MC_Main()
        {
            //remove the successful completion flag
            if (File.Exists("SuccessfulCompletion.txt"))
            {
                File.Delete("SuccessfulCompletion.txt");
            }

            //check that it will be possible to write to a database
            resultsDatabase MCResults = new resultsDatabase();

            MCResults.cleanUp();
            
            InteractWithModel.WhatModel();

            //use the commandString to get all the arguments
            CommandString cs = new CommandString();
            //use runString to get the right set of output files written, "-size none" during runs
            String runString;

            InteractWithModel.setRunMCParameters();
 
            cs.Populate();

            //this will need to be updated as more models are added
            switch(MCParameters.model)
            {
                case 1:
                case 8:
                case 10:
                    MCParameters.coefficientsFile = "PERSiST_Errors.csv";
                    break;
                default:
                    MCParameters.coefficientsFile = "coefficients.csv";
                    break;
            }
            
            InteractWithModel.SetLandUseAndReaches();

            //run the model once to ensure outputs exist
            runString = string.Concat(cs.commandLine, " -size none");
            Console.WriteLine(runString);
            
            InteractWithModel.RunModel(runString);

            InteractWithModel.SetCoefficientWeights();
            InteractWithModel.WriteCoefficientWeights();
            InteractWithModel.SetSeriesWeights();
            InteractWithModel.SetJumpSize();
            // 
            // sometimes default parameter file does not exist, causes MCNew to fail
            //
            MCMCController.MCNew("mc.csv", runString);
            MCParameters.runsToOrganize = MCParameters.maxTries;

            //now we need some output
            //so we need to reset the runstring
            
            runString = string.Concat(cs.commandLine, " -size ", MCParameters.outputSize, " ");
            //may be needed
            if (MCParameters.model == 1 ^ MCParameters.model == 8 ^ MCParameters.model==10 )
            {
                runString=string.Concat(runString, " -inca ", MCParameters.INCAOutputFile );
            }

            MCResults.processParameterData();
            MCResults.createParameterSensitivitySummaryTable();
            MCResults.writeCoefficientWeights();

            SummarizeResults.write(runString);
            SummarizeResults.noteSuccessfulCompletion();

            new PostProcessing().Run();

            Console.ReadLine();
        }
        
        static void GLUE_Main()
        {
            //remove the successful completion flag
            if (File.Exists("SuccessfulCompletion.txt"))
            {
                File.Delete("SuccessfulCompletion.txt");
            }
            InteractWithModel.setGLUECount();

            InteractWithModel.WhatModel();

            //use the commandString to get all the arguments
            CommandString cs = new CommandString();
            //use runString to get the right set of output files written, "-size none" during runs
            String runString;

            //InteractWithModel.setRunMCParameters();

            cs.Populate();

            switch(MCParameters.model)
            {
                case 1:
                case 8:
                case 10:
                    {
                        MCParameters.coefficientsFile = "PERSiST_Errors.csv";
                        break;
                    }
                default:
                    {
                        MCParameters.coefficientsFile = "coefficients.csv";
                        break;
                    }
            }
                
            InteractWithModel.SetLandUseAndReaches();

            //run the model once to ensure outputs exist
            runString = string.Concat(cs.commandLine, " -size none");
            Console.WriteLine(runString);

            InteractWithModel.RunModel(runString);
            InteractWithModel.SetCoefficientWeights();
            InteractWithModel.WriteCoefficientWeights();
            InteractWithModel.SetSeriesWeights();
            
            for(long i=0;i<MCParameters.GLUERuns; i++)
            {
                GLUEAccounting.GLUERun(runString, i);
            }

            new PostProcessing().Run(); 

            //write a note of successful completion
            if (!File.Exists("SuccessfulCompletion.txt"))
            {
                // Create a file to write to. 
                using (StreamWriter sw = File.CreateText("SuccessfulCompletion.txt"))
                {
                    sw.WriteLine("Successful Completion");
                    sw.WriteLine("Date: {0}", System.DateTime.Now.ToString());
                }
            }
        }
    }

}
