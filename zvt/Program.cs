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

        public const byte DLE = 0x10;
        public const byte ACK = 0x06;
        public const byte NAK = 0x15;

        static string portName = "com3";
        static string logs = @"z:\zvt_logs";

        static bool verbose = false;

        static int timeout = 60000;

        static string stopbits = "1";

        static readonly Dictionary<byte, byte> StatusInformationFieldLengths = new Dictionary<byte, byte>()
        {
            // Amount
            { 0x04, 6 },
            // Trace
            { 0x0B, 3 },
            // Orig. trace
            { 0x37, 3 },
            // Time
            { 0x0C, 3 },
            // Date
            { 0x0D, 2 },
            // Expiry date
            { 0x0E, 2 },
            // Sequence number
            { 0x17, 2 },
            // Payment type
            { 0x19, 1 },
            // PAN/EF_ID
            { 0x22, 0 /*LLVAR*/ },
            // Terminal-ID
            { 0x29, 4 },
            // AID
            { 0x3B, 8 },
            // CC
            { 0x49, 2 },
            // Blocked goods groups
            { 0x4C, 0 /*LLVAR*/ },
            // Receipt no.
            { 0x87, 2 },
            // Card type
            { 0x8A, 1 },
            // Card type ID
            { 0x8C, 1 },
            // Payment record
            { 0x9A, 103 },
            // AID parameter
            { 0xBA, 5 },
            // VU number
            { 0x2A, 15 },
            // Additional text
            { 0x3C, 0 /*LLLVAR*/ },
            // Result code AS
            { 0xA0, 1 },
            // Turnover no
            { 0x88, 3 },
            // Card name
            { 0x8B, 0 /*LLVAR*/ },
            // Additional data
            { 0x06, 0 /*TLV*/ },
        };

        static readonly Dictionary<byte, VariableLengthType> VariableLengthStatusInformationFields = new Dictionary<byte, VariableLengthType>()
        {
            // PAN/EF_ID
            { 0x22, VariableLengthType.LLVAR },
            // Blocked goods groups
            { 0x4C, VariableLengthType.LLVAR },
            // Additional text
            { 0x3C, VariableLengthType.LLLVAR },
            // Card name
            { 0x8B, VariableLengthType.LLVAR },
            // Additional data
            { 0x06, VariableLengthType.TLV },
        };

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
            catch { }
        }

        static ushort CalcCrc2(IEnumerable<byte> data)
        {
            int crc;
            var t = new int[256];

            for (var i = 0; i < 256; i++)
            {
                crc = i;

                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 1) > 0) crc = (crc >> 1) ^ 0x8408;
                    else crc = crc >> 1;
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

            return (ushort)((crc >> 8) | ((crc & 0xFF) << 8));
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

            var debugOutput = string.Join(" ", data.Select(i => string.Format("{0:X2}", i)));
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
                Debug("ERROR       : " + string.Join(" ", new int[] { a, b }.Select(i => string.Format("{0:X2}", i))) + "\r\n");

                throw new Exception();
            }

            var c0 = (byte)s.ReadByte();
            var c1 = (byte)s.ReadByte();
            var length = (byte)s.ReadByte();

            ushort actualLength = length;

            var arrayOffset = 3;

            var lengthArray = new byte[1] { length };

            if (length == 0xFF)
            {
                var loByte = (byte)s.ReadByte();
                var hiByte = (byte)s.ReadByte();

                actualLength = (ushort)((hiByte << 8) + loByte);
                arrayOffset = 5;
                lengthArray = new byte[3] { length, loByte, hiByte };
            }

            var data = new byte[arrayOffset + actualLength];

            data[0] = c0;
            data[1] = c1;

            for (int i = 0; i < lengthArray.Length; i++)
            {
                var value = lengthArray[i];

                data[2 + i] = value;
            }

            for (int i = 0; i < actualLength; i++)
            {
                var r = (byte)s.ReadByte();

                if (r == DLE) r = (byte)s.ReadByte();

                data[arrayOffset + i] = r;
            }

            var checksum = s.ReadByte() << 24 + s.ReadByte() << 16 + s.ReadByte() << 8 + s.ReadByte();

            Thread.Sleep(100);
            s.Write(new byte[] { 6 }, 0, 1);

            var debugOutput = string.Join(" ", data.Select(i => string.Format("{0:X2}", i)));
            Debug("TERM -> HOST: " + debugOutput + "\r\n");

            return data;
        }

        static bool Pay(SerialPort s, decimal amount, decimal cashbackAmount)
        {
            byte totalLength = 0x12;

            var payCommand = new List<byte>(new byte[] { 0x06, 0x01, totalLength, 0x04 });
            payCommand.AddRange(DecimalToBcd(amount));

            // TLV-Container Start
            payCommand.Add(0x06);
            payCommand.Add(0x09);
            // Cashback-Tag
            payCommand.Add(0x1F);
            payCommand.Add(0x25);
            // Length
            payCommand.Add(0x06);
            // Amount in BCD
            payCommand.AddRange(DecimalToBcd(cashbackAmount));

            SendRawData(s, payCommand);
            RecvRawData(s);

            var pay = RecvRawData(s);
            SendRawData(s, new byte[] { 0x80, 0x00, 0x00 });

            // Wenn length = 0xFF ist, sind 3 bytes für die länge und pay[6] muss auf 0 geprüft werden statt pay[4]
            if (pay[0] == 0x04 && pay[1] == 0x0F && (pay[2] == 0xFF && pay[6] == 0 || pay[2] != 0xFF && pay[4] == 0))
            {
                var lengthOffset = pay[2] == 0xFF ? 2 : 0;
                var extendedFailed = (pay.Length > 8 + lengthOffset) && pay[5 + lengthOffset] == 0x06 && pay[7 + lengthOffset] == 0x1F && pay[8 + lengthOffset] == 0x16;

                var isSuccessful = false;

                try
                {
                    var r = RecvRawData(s);
                    // Success = 0x06 0F 00
                    if (r[0] == 0x06 && r[1] == 0x0F && r[2] == 0x00) isSuccessful = true;

                    SendRawData(s, new byte[] { 0x80, 0x00, 0x00 });
                }
                catch (Exception ex)
                {
                    if (ex.Message != null) Debug($"ERROR       : {ex.Message}");
                }

                if (extendedFailed) return false;

                //if (isSuccessful)
                //{
                //    WritePayResultJson(pay, 5);
                //}

                return isSuccessful;
            }

            return false;
        }

        static void WritePayResultJson(byte[] message, int startIndex)
        {
            byte? cardType = null;

            for (int i = startIndex; i < message.Length; i++)
            {
                var bmpCode = message[i];

                if (bmpCode == 0x8A)
                {
                    cardType = message[i + 1];
                    break;
                }
                else
                {
                    var length = StatusInformationFieldLengths[bmpCode];

                    // Ist es ein Feld mit variabler Länge?
                    if (length == 0)
                    {
                        var variableLengthType = VariableLengthStatusInformationFields[bmpCode];

                        switch (variableLengthType)
                        {
                            // Beispiel LLVAR: F1 F0 = 10
                            case VariableLengthType.LLVAR:
                            {
                                var f1 = message[i + 1];
                                var f2 = message[i + 2];

                                f1 &= 0x0F;
                                f2 &= 0x0F;

                                length = (byte)(f1 * 10 + f2);
                                break;
                            }// Beispiel LLLVAR: F1 F0 F3 = 103
                            case VariableLengthType.LLLVAR:
                            {
                                var f1 = message[i + 1];
                                var f2 = message[i + 2];
                                var f3 = message[i + 3];

                                f1 &= 0x0F;
                                f2 &= 0x0F;
                                f3 &= 0x0F;

                                length = (byte)(f1 * 100 + f2 * 10 + f3);

                                break;
                            }
                            case VariableLengthType.TLV:
                            {
                                // TLV-Container sollten erst kommen sobald wir unsere Information haben
                                return;
                            }// Bei nicht definiertem Fall müssen wir abbrechen
                            default: return;
                        }
                    }

                    i += length;
                }
            }

            // Wenn wir keinen Karten-Typen bekommen haben (warum auch immer) brechen wir ab
            if (cardType == null) return;

            var path = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "result.json");

            var json = "{\"CardType\":" + $"\"{cardType}\"" + "}";

            File.WriteAllText(path, json);
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
                    {
                        Registration(s);

                        var paymentAmount = decimal.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);

                        var cashbackAmount = 0.0m;

                        if (args.Length > 2)
                        {
                            var secondCommand = args[2].ToLower();

                            if (secondCommand == "-cashback")
                            {
                                cashbackAmount = decimal.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
                            }
                        }

                        if (Pay(s, paymentAmount, cashbackAmount) == true) exitCode = 0;
                        break;
                    }
                    case "-endofday":
                        Registration(s);

                        if (EndOfDay(s) == true) exitCode = 0;

                        //LogOff(s);
                        break;
                }
            }
            catch (Exception e)
            {
                Debug(e.Message + "\r\n" + e.StackTrace);

                Console.WriteLine("" + e.Message);
            }
            finally
            {
                if (s != null) s.Close();
            }

            Console.WriteLine("Exit code = " + exitCode);

            Debug("Exit code = " + exitCode);

            File.WriteAllText(Path.Combine(path, "result.txt"), exitCode.ToString());

            return exitCode;
        }
    }

    public enum VariableLengthType
    {
        LLVAR = 0,
        LLLVAR = 1,
        TLV = 2
    }
}