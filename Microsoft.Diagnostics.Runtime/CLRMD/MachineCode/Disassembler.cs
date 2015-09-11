using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace MachineCode
{
    internal class AssemblyLine
    {
        public uint Address { get; set; }
        public string RawData { get; set; }
        public string Text { get; set; }
        public string Instruction { get; set; }
        public string Parameters { get; set; }

        public override string ToString()
        {
            // Print the output how VS does
            //00000000  push        ebp 
            //00000001  mov         ebp,esp 
            //00000003  push        edi 
            if (Instruction == null || Parameters == null)
                return String.Format("{0:X8}  {1}{2}", Address, RawData.PadRight(18), Text);
            return String.Format("{0:X8}  {1}{2}", Address, Instruction.PadRight(12), Parameters);
        }

        public string ToNasmString()
        {
            //00000000  55                push ebp
            //00000001  8BEC              mov ebp,esp
            //0000000A  E879BBFA70        call dword 0x70fabb88
            return String.Format("{0:X8}  {1}{2}", Address, RawData.PadRight(18), Text);
        }
    }

    internal static class Disassembler
    {
        // From http://stackoverflow.com/questions/139593/processstartinfo-hanging-on-waitforexit-why/7608823#7608823
        internal static string GetDisassembly(string filename, string arguments, int timeoutMsecs)
        {
            using (var process = new Process())
            {
                process.StartInfo.FileName = filename;
                process.StartInfo.Arguments = arguments;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;

                var output = new StringBuilder();
                var error = new StringBuilder();

                using (var outputWaitHandle = new AutoResetEvent(false))
                using (var errorWaitHandle = new AutoResetEvent(false))
                {
                    process.OutputDataReceived += (sender, e) =>
                    {
                        if (e.Data == null)
                        {
                            if (outputWaitHandle != null) outputWaitHandle.Set();
                        }
                        else
                        {
                            output.AppendLine(e.Data);
                        }
                    };
                    process.ErrorDataReceived += (sender, e) =>
                    {
                        if (e.Data == null)
                        {
                            if (errorWaitHandle != null) errorWaitHandle.Set();
                        }
                        else
                        {
                            error.AppendLine(e.Data);
                        }
                    };

                    process.Start();

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (process.WaitForExit(timeoutMsecs) &&
                        outputWaitHandle.WaitOne(timeoutMsecs) &&
                        errorWaitHandle.WaitOne(timeoutMsecs))
                    {
                        // Process completed. Check process.ExitCode here.
                    }
                    else
                    {
                        // Timed out.
                        Console.WriteLine("TIMED OUT!!");
                    }
                }
                if (error.Length > 0)
                    Console.WriteLine(error.ToString());

                return output.ToString();
            }
        }

        internal static List<AssemblyLine> ProcessDisassembly(string disassembly)
        {
            var assemblyData = new List<AssemblyLine>();
            var disassemblyLines = disassembly.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in disassemblyLines)
            {
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3)
                {
                    Console.WriteLine("Unexpected line: " + line);
                    continue;
                }

                var address = Convert.ToUInt32(parts[0], 16);
                var rawData = parts[1];
                var textStart = line.IndexOf(parts[2]);
                var text = line.Substring(textStart);

                // Test our error checking!!!
                //if (rawData == "53")
                //    text = "push blah";

                String instruction = null, parameters = null;
                var firstSpace = text.IndexOf(' ');
                if (firstSpace != -1)
                {
                    instruction = text.Substring(0, firstSpace);
                    parameters = text.Substring(firstSpace, text.Length - firstSpace);
                }

                var newData = new AssemblyLine
                    {
                        Address = address,
                        RawData = rawData,
                        Text = text,
                        Instruction = instruction,
                        Parameters = parameters
                    };
                Console.WriteLine(newData);
                assemblyData.Add(newData);
            }

            int lineNum = 0;
            foreach (var line in disassemblyLines)
            {
                var other = assemblyData[lineNum];
                if (other.ToNasmString() != line)
                    Console.WriteLine("Error on line {0}\nExpected:{1}\n     Got:{2}", lineNum, line, other.ToString());
                lineNum++;
            }
            return assemblyData;
        }
    }
}
