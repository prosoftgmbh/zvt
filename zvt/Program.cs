using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.IO.Ports;
using System.Threading;
using IniParser;
using IniParser.Model;
using System.Reflection;

namespace zvt
{
    class Program
    {
        static string debugFile;

        public static void Debug(string format)
        {
            if (!verbose) return;

            // LOG-Verzeichnis erstellen falls notwendig
            if (!Directory.Exists(logs)) Directory.CreateDirectory(logs);


            if (debugFile == null) debugFile = Path.Combine(logs, string.Format("zvt_{0:yyyyMMddhhmmss}.log", DateTime.Now));

            try
            {
                File.AppendAllText(debugFile, format);
            }
            catch
            {
            }
        }

        public const byte DLE = 0x10;

        public const byte ACK = 0x06;

        public const byte NAK = 0x15;



        static ushort CalcCrc2(IEnumerable<byte> data)
        {
            int crc;
            var t = new int[256];

            for (var i = 0; i < 256; i++)
            {
                crc = i;

                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 1) > 0)
                        crc = (crc >> 1) ^ 0x8408;
                    else
                        crc = crc >> 1;
                }

                t[i] = crc;
            }

            crc = 0;

            var l = new List<byte>(data);
            l.Add(0x03);

            for (int i = 0; i < l.Count; i++)
            {
                var hb = crc >> 8;
                var lb = crc & 0xFF;

                crc = t[lb ^ l[i]] ^ hb;
            }

            return (ushort) ((crc >> 8) | ((crc & 0xFF) << 8));
        }


        static byte[] DecimalToBcd(decimal value)
        {
            int v = (int)(decimal.Round(value, 2, MidpointRounding.AwayFromZero) * 100);

            var ret = new byte[6];

            for (int i = 0; i < ret.Length; i++)
            {
                ret[i] = (byte)(v % 10);
                v /= 10;

                ret[i] |= (byte)((v % 10) << 4);
                v /= 10;
            }

            return ret.Reverse().ToArray();
        }



        static int SendRawData(SerialPort s, IEnumerable<byte> data)
        {
            Thread.Sleep(100);

            //var checksum = CalcCrc(data);
            var checksum = CalcCrc2(data);

            s.Write(new byte[] { 0x10, 0x02 }, 0, 2);

            var l = new List<byte>();
            foreach (var b in data)
            {
                l.Add(b);

                if (b == DLE) l.Add(b);
            }
            s.Write(l.ToArray(), 0, l.Count);

            s.Write(new byte[] { 0x10, 0x03 }, 0, 2);
            //1CA2
            var cs2 = new byte[] { (byte)(checksum >> 8), (byte)(checksum & 0xFF) };
            var cs = new byte[] { 0x1C, 0xA2 };
            s.Write(cs2, 0, 2);

            var debugOutput  = string.Join(" ", data.Select(i => string.Format("{0:X2}", i)));
            Debug("HOST -> TERM: " + debugOutput + "\r\n");

            Thread.Sleep(100);

            return s.ReadByte();
        }

        static byte[] RecvRawData(SerialPort s)
        {
            Thread.Sleep(100);

            var a = s.ReadByte();
            var b = s.ReadByte();

            if (a != 0x10 || b != 0x02)
            {
                Debug("ERROR       : " + string.Join(" ", new int[] {a, b}.Select(i => string.Format("{0:X2}", i))) + "\r\n");

                throw new Exception();
            }

            var c0 = (byte)s.ReadByte();
            var c1 = (byte)s.ReadByte();
            var length = (byte)s.ReadByte();

            var data = new byte[3 + length];

            data[0] = c0;
            data[1] = c1;
            data[2] = length;

            for (int i = 0; i < length; i++)
            {
                var r = (byte)s.ReadByte();

                if (r == DLE) r = (byte)s.ReadByte();
                     
                data[3 + i] = r;
            }

            var checksum = s.ReadByte() << 24 + s.ReadByte() << 16 + s.ReadByte() << 8 + s.ReadByte();

            Thread.Sleep(100);
            s.Write(new byte[] { 6 }, 0, 1);

            var debugOutput = string.Join(" ", data.Select(i => string.Format("{0:X2}", i)));
            Debug("TERM -> HOST: " + debugOutput + "\r\n");

            return data;
        }




        static bool Pay(SerialPort s, decimal amount)
        {
            var payCommand = new List<byte>(new byte[] { 0x06, 0x01, 0x07, 0x04 });
            payCommand.AddRange(DecimalToBcd(amount));

            int start = SendRawData(s, payCommand);
            var start_recv = RecvRawData(s);

            var pay = RecvRawData(s);
            SendRawData(s, new byte[] { 0x80, 0x00, 0x00 });
            if (pay[0] == 0x04 && pay[1] == 0x0F && pay[4] == 0)
            {
                var isSuccessful = false;

                try
                {
                    var r = RecvRawData(s);

                    if (r[0] == 0x60 && r[1] == 0x0F) isSuccessful = true;

                    SendRawData(s, new byte[] { 0x80, 0x00, 0x00 });
                }
                catch
                {
                }

                return isSuccessful;
            }

            return false;
        }

        static bool EndOfDay(SerialPort s)
        {
            int reg = SendRawData(s, new byte[] { 0x06, 0x50, 0x03, 0x00, 0x00, 0x00 });

            if (reg != 0x06) return false;

            var reg_recv = RecvRawData(s);

            if (reg_recv[0] != 0x80 || reg_recv[1] != 0x00 || reg_recv[2] != 0x00) return false;

            var pay = RecvRawData(s);
            SendRawData(s, new byte[] { 0x80, 0x00, 0x00 });

            return pay[0] == 0x04 && pay[1] == 0x0F;
        }



        static void Registration(SerialPort s)
        {
            SendRawData(s, new byte[] { 0x06, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00 });
            RecvRawData(s);

            RecvRawData(s);
            SendRawData(s, new byte[] { 0x80, 0x00, 0x00 });
        }

        static void LogOff(SerialPort s)
        {
            int r = SendRawData(s, new byte[] { 0x06, 0x02, 0x00 });
            var a = RecvRawData(s);
        }


        static string portName = "com3";

        static string logs = @"z:\zvt_logs";

        static bool verbose = false;

        static int timeout = 60000;

        static string stopbits = "1";

        static int Main(string[] args)
        {
            var parser = new FileIniDataParser();

            IniData data;

            var path = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            try
            {
                data = parser.ReadFile(Path.Combine(path, "zvt.ini"));

                portName = data["settings"]["port"];

                stopbits = data["settings"]["stopbits"];

                verbose = bool.Parse(data["settings"]["verbose"]);

                timeout = int.Parse(data["settings"]["timeout"]) * 1000;

                logs = data["settings"]["logs"];
            }
            catch
            {
                Console.WriteLine("ERROR: Unable to parse configuration file 'zvt.ini'");

                return -1;
            }

            int exitCode = 2;

            var command = args.Length == 0 ? null : args[0].ToLower();

            if (command != "-pay" && command != "-endofday")
            {
                Console.WriteLine("Usage: zvt.exe -pay amount");
                Console.WriteLine("Usage: zvt.exe -endofday");

                return 1;
            }

            SerialPort s = null;

            try
            {
                StopBits sb;

                switch (stopbits.Trim())
                {
                    case "0": sb = StopBits.None; break;
                    case "1": sb = StopBits.One; break;
                    case "1.5": sb = StopBits.OnePointFive; break;
                    case "2": sb = StopBits.Two; break;
                    default: throw new ArgumentOutOfRangeException("stopbits");
                }

                s = new SerialPort(portName, 9600, Parity.None, 8, sb)
                {
                    ReceivedBytesThreshold = 1,
                    ReadTimeout = timeout
                };

                s.Open();

                int reg = SendRawData(s, new byte[] { 0x06, 0x00, 0x00 });
                var reg_recv = RecvRawData(s);

                /*var r = RecvRawData(s);
                SendRawData(s, new byte[] { 0x80, 0x00, 0x00 });*/


                /*var x = SendRawData(s, new byte[] { 0x06, 0xE0, 0x02, 0xF0, 0x00 });
                var y = RecvRawData(s);*/

                /*var x = SendRawData(s, new byte[] { 0x06, 0xB0, 0x00 });
                var y = RecvRawData(s);*/

                switch (command)
                {
                    case "-pay":
                        Registration(s);

                        if (Pay(s, decimal.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture)) == true) exitCode = 0;
                        break;

                    case "-endofday":
                        Registration(s);

                        if (EndOfDay(s) == true) exitCode = 0;

                        //LogOff(s);
                        break;
                }
            }
            catch(Exception e)
            {
                Debug(e.Message + "\r\n" + e.StackTrace);

                Console.WriteLine("" + e.Message);
            }
            finally
            {
                if(s != null) s.Close();
            }

            Console.WriteLine("Exit code = " + exitCode);

            Debug("Exit code = " + exitCode);

            File.WriteAllText(Path.Combine(path, "result.txt"), exitCode.ToString());

            return exitCode;
        }
    }
}
