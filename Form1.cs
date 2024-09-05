using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Mitsubishi_FX
{
    public partial class Form1 : Form
    {
        MFX_Protocol CPU;


        public Form1()
        {
            //create a serial Transport layer instance (which will be used by the procotol instance to communicate with the PLC)
            MFX_SerialTP serial = new MFX_SerialTP("COM5", 38400, System.IO.Ports.Parity.Even, System.IO.Ports.StopBits.One, 7, 1000);

            CPU = new MFX_Protocol(serial);
            CPU.Start();

            ushort[] Data = new ushort[] { 1000, 2000 };
            var Result = CPU.WriteNumericData_16B(RegisterType.Data, 20, 2, Data);

            ushort[] ReadData;
            var Result = CPU.ReadNumericData_16B(RegisterType.Counter_16B, 6, 3,out ReadData);    

            bool[] ReadData;
            var Result = CPU.ReadBitData(RegisterType.Output_Contact, 10, 2, out ReadData);

            int Offset = 0;

            ushort[] Tdata = new ushort[32];
            MFX_Protocol.EncodeFloat(ref Tdata, Offset, 1.25f); Offset += 2;
            MFX_Protocol.EncodeUInt32(ref Tdata, Offset, 150000); Offset += 2;
            MFX_Protocol.EncodeInt32(ref Tdata, Offset, -160000); Offset += 2;


            var t = CPU.WriteNumericData_16B(RegisterType.Data, 0, (byte)Offset, Tdata);

            ushort[] Data = new ushort[32];

            var Result = CPU.ReadNumericData_16B(RegisterType.Data, 0, 8, out Data);
            Offset = 0;

            float F = MFX_Protocol.ParseFloat(Data, Offset); Offset += 2;
            UInt32 G = MFX_Protocol.ParseUInt32(Data, Offset); Offset += 2;
            Int32 H = MFX_Protocol.ParseInt32(Data, Offset); Offset += 2;

            var Result = CPU.Force_Bit(RegisterType.Memory_Contact, 500, true);

            InitializeComponent();
        }
    }
}
