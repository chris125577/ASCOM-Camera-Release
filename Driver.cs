//tabs=4
// --------------------------------------------------------------------------------
//
// ASCOM Camera driver for USB
//
// Description:	Simple USB relay driver for camera operation
//
// Implements:	ASCOM Camera interface version: <1.0>
// Author:		(CJW) Chris Woodhouse <cwoodhou@icloud.com>
//
// Edit Log:
//
// Date			Who	Vers	Description
// -----------	---	-----	-------------------------------------------------------
// 05-Sep-2019	CJW	1.0.0	Initial edit, created from ASCOM driver template - conformed
// 12-Sep-2019  CJW 1.1.1   Adding in larger file-based image and tidying up abort, stop and gets
// 14-Sep-2019  CJW 1.2     Working with % complete and file transfer to imaging app
// May 2020 -   CJW 1.3     scaled 8-bit dummy image, as I realized that file transfer assumes 16-bit format.
// Sep 2023 -   CJW 1.4     added extra ASCOM settings for pixel sizes - dummy camera file is now called dummy.jpg
// --------------------------------------------------------------------------------
//


// This is used to define code in the template that is specific to one class implementation
// unused code can be deleted and this definition removed.
#define Camera

using System;
using System.Runtime.InteropServices;
using ASCOM.Astrometry.AstroUtils;
using ASCOM.Utilities;
using ASCOM.DeviceInterface;
using System.Globalization;
using System.Collections;
using System.IO.Ports;
using System.Timers;
using System.Drawing;

namespace ASCOM.USB
{
    //
    // Your driver's DeviceID is ASCOM.USB.Camera
    //
    // The Guid attribute sets the CLSID for ASCOM.USB.Camera
    // The ClassInterface/None addribute prevents an empty interface called
    // _USB from being created and used as the [default] interface
  

    /// <summary>
    /// ASCOM Camera Driver for USB.
    /// </summary>
    [Guid("ac994c88-766c-4137-a09e-860a94bc4bdd")]
    [ClassInterface(ClassInterfaceType.None)]
    public class Camera : ICameraV2
    {
        /// <summary>
        /// ASCOM DeviceID (COM ProgID) for this driver.
        /// The DeviceID is used by ASCOM applications to load the driver at runtime.
        /// </summary>
        internal static string driverID = "ASCOM.USB.Camera";
        internal static string driverDescription = "ASCOM Cable Release.";

        internal static string comPortProfileName = "COM Port"; // Constants used for Profile persistence
        internal static string comPortDefault = "COM1";
        internal static string pixelsizeDefault = "5.0";
        internal static string pixelwidthDefault = "600";
        internal static string pixelheightDefault = "400";
        internal static string traceStateProfileName = "Trace Level";
        internal static string traceStateDefault = "false";
        internal static string comPort; // Variables to hold the currrent device configuration
        internal static double  pixelSize;
        internal static int ccdWidth, ccdHeight;

        // for serial commands kmtronic
        internal byte[] relaystatus = new byte[4]; // up to four characters in status string
        internal byte[] command = new byte[4]; // up to four characters
        internal byte txstringlen = 3;  // number of characters in kmtronic command string
        internal byte rxstringlen = 3; // number of characters in kmtronic reply
        internal byte startByte = 0xFF;  // start character of kmtronic relay command
        internal byte relaynumber = 0x01; // only one relay (kmtronic)
        internal byte setpin = 0x01; // kmtronic
        internal byte clearpin = 0x00;  // kmtronic control bytes to set and reset relay
        internal byte readpin = 0x03; // kmtronic
        private SerialPort Serial; // my serial port instance of ASCOM serial port
        /// <summary>
        /// Private variable to hold the connected state
        /// </summary>
        private bool connectedState = false;

        /// <summary>
        /// Private variable to hold an ASCOM Utilities object
        /// </summary>
        private Util utilities;

        /// <summary>
        /// Private variable to hold an ASCOM AstroUtilities object to provide the Range method
        /// </summary>
        private AstroUtils astroUtilities;

        /// <summary>
        /// Variable to hold the trace logger object (creates a diagnostic log file with information that you specify)
        /// </summary>
        internal static TraceLogger tl;

        /// <summary>
        /// Initializes a new instance of the <see cref="USB"/> class.
        /// Must be public for COM registration.
        /// </summary>
        public Camera()
        {
            tl = new TraceLogger("", "USB");
            tl.Enabled = true;
            ReadProfile(); // Read device configuration from the ASCOM Profile store
            tl.LogMessage("Camera", "Starting initialisation");
            utilities = new Util(); //Initialise util object
            astroUtilities = new AstroUtils(); // Initialise astro utilities object
            Serial = new SerialPort(); // standard .net serial port
            tl.LogMessage("Camera", "Completed initialisation");
        }
        // sets up the serial port in the FTDI virtual com port on the relay board
        private bool SetupSwitch()
        {
            Serial.BaudRate = 9600;
            Serial.PortName = comPort;
            Serial.Parity = Parity.None;
            Serial.DataBits = 8;
            Serial.Handshake = System.IO.Ports.Handshake.None;
            Serial.ReceivedBytesThreshold = 1;
            try
            { Serial.Open();  // open port
                Serial.DiscardInBuffer();   // clear out just in case
            }
            catch (Exception)
            {
                return false;
            }
            return true;  
        }
       
        // sets relay kmtronic
        private void SwitchOn()
        {
            if (connectedState)
            {
                byte[] relaycmd = new byte[4];
                relaycmd[0] = startByte;
                relaycmd[1] = relaynumber;  // relay 1 (only 1)
                relaycmd[2] = setpin;  // byte for setting
                //relaycmd[4] = something;
                Serial.Write(relaycmd, 0, txstringlen);  // send command
                tl.LogMessage("SetSwitch", "0");
            }
        }
   
        // resets relay kmtronic
        private void SwitchOff()
        {
            if (connectedState)
            {
                byte[] relaycmd = new byte[4];
                relaycmd[0] = startByte;
                relaycmd[1] = relaynumber;  // relay 1 (only 1)
                relaycmd[2] = clearpin;  // byte for setting
                //relaycmd[4] = something;
                Serial.Write(relaycmd, 0, txstringlen);  // send command
                tl.LogMessage("SetSwitch", "0");
            }
        }

        // checks relay status kmtronic
        private bool GetSwitch()
        {
            try
            {
                Serial.DiscardInBuffer(); // clear garbage
                byte[] relaycmd = new byte[4];
                relaycmd[0] = startByte;
                relaycmd[1] = relaynumber;  // relay 1 (only 1)
                relaycmd[2] = readpin;  // byte for setting
                //relaycmd[4] = something;
                Serial.Write(relaycmd, 0, txstringlen); // send read relay command
                for (int i = 0; i < rxstringlen; i++)  // read in rxstringlen  bytes  FF 01 xx
                {
                    relaystatus[i] = (byte)Serial.ReadByte();
                }
                return (relaystatus[2] > 0); // you may need to check if this works.
            }
            catch
            {
                tl.LogMessage("GetSwitch", string.Format("GetSwitch() - not implemented"));
                throw new ASCOM.NotConnectedException("GetSwitch");
            }
        }
            //
            // PUBLIC COM INTERFACE ICameraV2 IMPLEMENTATION
            //

            #region Common properties and methods.

            /// <summary>
            /// Displays the Setup Dialog form.
            /// If the user clicks the OK button to dismiss the form, then
            /// the new settings are saved, otherwise the old values are reloaded.
            /// THIS IS THE ONLY PLACE WHERE SHOWING USER INTERFACE IS ALLOWED!
            /// </summary>
            public void SetupDialog()
        {
            // consider only showing the setup dialog if not connected
            // or call a different dialog if connected
            if (connectedState)
                System.Windows.Forms.MessageBox.Show("Already connected, just press OK");

            using (SetupDialogForm F = new SetupDialogForm())
            {
                var result = F.ShowDialog();
                if (result == System.Windows.Forms.DialogResult.OK)
                {
                    WriteProfile(); // Persist device configuration values to the ASCOM Profile store
                }
            }
        }

        public ArrayList SupportedActions
        {
            get
            {
                tl.LogMessage("SupportedActions Get", "Returning empty arraylist");
                return new ArrayList();
            }
        }

        public string Action(string actionName, string actionParameters)
        {
            LogMessage("", "Action {0}, parameters {1} not implemented", actionName, actionParameters);
            throw new ASCOM.ActionNotImplementedException("Action " + actionName + " is not implemented by this driver");
        }

        public void CommandBlind(string command, bool raw)
        {
            CheckConnected("CommandBlind");
            throw new ASCOM.MethodNotImplementedException("CommandBlind");
        }

        public bool CommandBool(string command, bool raw)
        {
            CheckConnected("CommandBool");
            string ret = CommandString(command, raw);
            throw new ASCOM.MethodNotImplementedException("CommandBool");
        }

        public string CommandString(string command, bool raw)
        {
            CheckConnected("CommandString");
            throw new ASCOM.MethodNotImplementedException("CommandString");
        }

        public void Dispose()
        {
            // Clean up the tracelogger and util objects
            tl.Enabled = false;
            tl.Dispose();
            tl = null;
            utilities.Dispose();
            utilities = null;
            astroUtilities.Dispose();
            astroUtilities = null;
            Serial.Dispose();
            if (exposureTimer != null) exposureTimer.Dispose();
        }

        public bool Connected
        {
            get
            {
                tl.LogMessage("Connected", "Get {0}", connectedState);
                return connectedState;
            }
            set
            {
                tl.LogMessage("Connected", "Set {0}", value);
                connectedState = value;
                if (value)
                {
                    LogMessage("Connected Set", "Connecting to port {0}", comPort);
                    SetupSwitch();  // turn on serial and connect
                    tl.LogMessage("image","trying");
                    ReadImageFile();  // read image file
                }
                else
                {
                    LogMessage("Connected Set", "Disconnecting from port {0}", comPort);
                    Serial.Close();
                    Serial.Dispose();  // disconnect serial port
                }
            }
        }

        public string Description
        {
           get
            {
                tl.LogMessage("Description Get", driverDescription);
                return driverDescription;
            }
        }

        public string DriverInfo
        {
            get
            {
                Version version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                string driverInfo = "Simple USB camera V 1.0" + String.Format(CultureInfo.InvariantCulture, "{0}.{1}", version.Major, version.Minor);
                tl.LogMessage("DriverInfo Get", driverInfo);
                return driverInfo;
            }
        }

        public string DriverVersion
        {
            get
            {
                Version version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                string driverVersion = String.Format(CultureInfo.InvariantCulture, "{0}.{1}", version.Major, version.Minor);
                tl.LogMessage("DriverVersion Get", driverVersion);
                return driverVersion;
            }
        }

        public short InterfaceVersion
        {
            // set by the driver wizard
            get
            {
                LogMessage("InterfaceVersion Get", "2");
                return Convert.ToInt16("2");
            }
        }

        public string Name
        {
            get
            {
                string name = "USB Cable Release";
                tl.LogMessage("Name Get", name);
                return name;
            }
        }

        #endregion

        #region ICamera Implementation
        // working variable for the camera and timer system
        private System.Timers.Timer exposureTimer; // for timing the exposure
        private DateTime exposureStartTime;
        private double exposureDuration;
        private double cameraLastExposureDuration = 0.0;
        internal short gain = 10;
        internal CameraStates status;
        private int cameraNumX = Camera.ccdWidth; // Initialise variables to hold values required for functionality tested by Conform
        private int cameraNumY = Camera.ccdHeight;
        private int cameraStartX = 0;
        private int cameraStartY = 0;

        // camera image
        internal String imagePath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "\\ASCOM\\Camera\\dummy.jpg";
        private Bitmap bmp;
        private float[,,] imageData;    // room for a monochrome image
        private int[,] imageArray;  // array for sending image over
        private bool cameraImageReady = false;
        private object[,] imageArrayVariant;

        public void AbortExposure()
        {
            if (connectedState)
            {
                
                switch (status)
                {
                    case CameraStates.cameraWaiting:
                    case CameraStates.cameraExposing:
                    case CameraStates.cameraReading:
                    case CameraStates.cameraDownload:
                        // these are all possible exposure states so we can abort the exposure
                        exposureTimer.Enabled = false;
                        status = CameraStates.cameraIdle;
                        SwitchOff();  // turn relay off
                        cameraImageReady = false;
                        tl.LogMessage("AbortExposure", "start");
                        break;
                    case CameraStates.cameraIdle:
                        break;
                    case CameraStates.cameraError:
                        tl.LogMessage("AbortExposure", "Camera Error");
                        throw new ASCOM.InvalidOperationException("AbortExposure not possible because of an error");
                }
                tl.LogMessage("AbortExposure", "done");
            }
        }

        public short BayerOffsetX
        {
            get
            {
                tl.LogMessage("BayerOffsetX Get Get", "Not implemented");
                throw new ASCOM.PropertyNotImplementedException("BayerOffsetX", false);
            }
        }

        public short BayerOffsetY
        {
            get
            {
                tl.LogMessage("BayerOffsetY Get Get", "Not implemented");
                throw new ASCOM.PropertyNotImplementedException("BayerOffsetX", true);
            }
        }

        public short BinX
        {
            get
            {
                tl.LogMessage("BinX Get", "1");
                return 1;
            }
            set
            {
                tl.LogMessage("BinX Set", value.ToString());
                if (value != 1) throw new ASCOM.InvalidValueException("BinX", value.ToString(), "1"); // Only 1 is valid in this simple template
            }
        }

        public short BinY
        {
            get
            {
                tl.LogMessage("BinY Get", "1");
                return 1;
            }
            set
            {
                tl.LogMessage("BinY Set", value.ToString());
                if (value != 1) throw new ASCOM.InvalidValueException("BinY", value.ToString(), "1"); // Only 1 is valid in this simple template
            }
        }

        public double CCDTemperature
        {
            get
            {
                tl.LogMessage("CCDTemperature Get Get", "Not implemented");
                throw new ASCOM.PropertyNotImplementedException("CCDTemperature", false);
            }
        }

        public CameraStates CameraState
        {
            get
            {
                if (!connectedState)
                {
                    tl.LogMessage("camera state", "Not connected");
                    throw new ASCOM.PropertyNotImplementedException("camera status", false);
                }
                else
                {
                    tl.LogMessage("CameraState Get", status.ToString());
                    return status;
                }
            }
        }

        public int CameraXSize
        {
            get
            {
                tl.LogMessage("CameraXSize Get", ccdWidth.ToString());
                return ccdWidth;
            }
        }

        public int CameraYSize
        {
            get
            {
                tl.LogMessage("CameraYSize Get", ccdHeight.ToString());
                return ccdHeight;
            }
        }

        public bool CanAbortExposure
        {
            get
            {
                tl.LogMessage("CanAbortExposure Get", true.ToString());
                return true;
            }
        }

        public bool CanAsymmetricBin
        {
            get
            {
                tl.LogMessage("CanAsymmetricBin Get", false.ToString());
                return false;
            }
        }

        public bool CanFastReadout
        {
            get
            {
                tl.LogMessage("CanFastReadout Get", false.ToString());
                return true;
            }
        }

        public bool CanGetCoolerPower
        {
            get
            {
                tl.LogMessage("CanGetCoolerPower Get", false.ToString());
                return false;
            }
        }

        public bool CanPulseGuide
        {
            get
            {
                tl.LogMessage("CanPulseGuide Get", false.ToString());
                return false;
            }
        }

        public bool CanSetCCDTemperature
        {
            get
            {
                tl.LogMessage("CanSetCCDTemperature Get", false.ToString());
                return false;
            }
        }

        public bool CanStopExposure
        {
            get
            {
                tl.LogMessage("CanStopExposure Get", true.ToString());
                return true;
            }
        }

        public bool CoolerOn
        {
            get
            {
                tl.LogMessage("CoolerOn Get Get", "Not implemented");
                throw new ASCOM.PropertyNotImplementedException("CoolerOn", false);
            }
            set
            {
                tl.LogMessage("CoolerOn Set Get", "Not implemented");
                throw new ASCOM.PropertyNotImplementedException("CoolerOn", true);
            }
        }

        public double CoolerPower
        {
            get
            {
                tl.LogMessage("CoolerPower Get Get", "Not implemented");
                throw new ASCOM.PropertyNotImplementedException("CoolerPower", false);
            }
        }

        public double ElectronsPerADU
        {
            get
            {
                tl.LogMessage("ElectronsPerADU Get Get", "Not implemented");
                throw new ASCOM.PropertyNotImplementedException("ElectronsPerADU", false);
            }
        }

        public double ExposureMax
        {
            get
            {
                tl.LogMessage("ExposureMax Get Get", "20 mins");
                return 2400.0;
            }
        }

        public double ExposureMin
        {
            get
            {
                tl.LogMessage("ExposureMin Get", "1 second");
                return 1.0;
            }
        }

        public double ExposureResolution
        {
            get
            {
                return (0.5);  // half second minimum timer resolution
            }
        }

        public bool FastReadout
        {
            get
            {
                tl.LogMessage("FastReadout Get", "Not implemented");
                return true;
            }
            set
            {
                tl.LogMessage("FastReadout Set", "Not implemented");
            }
        }

        public double FullWellCapacity
        {
            get
            {
                tl.LogMessage("FullWellCapacity Get", "Not implemented");
                throw new ASCOM.PropertyNotImplementedException("FullWellCapacity", false);
            }
        }

        public short Gain
        {
            get
            {
                tl.LogMessage("Gain Get", "Fixed value 1");
                return gain;
            }
            set
            {
                tl.LogMessage("Gain Set", "Not implemented");
                gain = value;
            }
        }

        public short GainMax
        {
            get
            {
                tl.LogMessage("GainMax Get", "Not implemented");
                return (10);
            }
        }

        public short GainMin
        {
            get
            {
                tl.LogMessage("GainMin Get", "Not implemented");
                return (0);
            }
        }

        public ArrayList Gains
        {
            get                
            {
                tl.LogMessage("Gains Get", "Not implemented");
                throw new ASCOM.PropertyNotImplementedException("Gains", false);
            }
        }

        public bool HasShutter
        {
            get
            {
                tl.LogMessage("HasShutter Get", false.ToString());
                return false;
            }
        }

        public double HeatSinkTemperature
        {
            get
            {
                tl.LogMessage("HeatSinkTemperature Get", "Not implemented");
                throw new ASCOM.PropertyNotImplementedException("HeatSinkTemperature", false);
            }
        }

        public object ImageArray
        {
            get
            {
                if (!cameraImageReady)
                {
                    tl.LogMessage("ImageArray Get", "Throwing InvalidOperationException because of a call to ImageArray before the first image has been taken!");
                    throw new ASCOM.InvalidOperationException("Call to ImageArray before the first image has been taken!");
                }
                return imageArray;
            }
        }

        public object ImageArrayVariant
        {
            get
            {
                if (!cameraImageReady)
                {
                    tl.LogMessage("ImageArrayVariant Get", "Throwing InvalidOperationException because of a call to ImageArrayVariant before the first image has been taken!");
                    throw new ASCOM.InvalidOperationException("Call to ImageArrayVariant before the first image has been taken!");
                }
                imageArrayVariant = new object[cameraNumX, cameraNumY];
                for (int i = 0; i < imageArray.GetLength(1); i++)
                {
                    for (int j = 0; j < imageArray.GetLength(0); j++)
                    {
                        imageArrayVariant[j, i] = 256*imageArray[j, i]; // 256 makes it 16 bit?
                    }
                }
                return imageArrayVariant;
            }
        }

        public bool ImageReady
        {
            get
            {
                tl.LogMessage("ImageReady Get", cameraImageReady.ToString());
                return cameraImageReady;
            }
        }

        public bool IsPulseGuiding
        {
            get
            {
                tl.LogMessage("IsPulseGuiding Get", "Not implemented");
                throw new ASCOM.PropertyNotImplementedException("IsPulseGuiding", false);
            }
        }

        public double LastExposureDuration
        {
            get
            {
                if (!cameraImageReady)
                {
                    tl.LogMessage("LastExposureDuration Get", "Throwing InvalidOperationException because of a call to LastExposureDuration before the first image has been taken!");
                    throw new ASCOM.InvalidOperationException("Call to LastExposureDuration before the first image has been taken!");
                }
                tl.LogMessage("LastExposureDuration Get", cameraLastExposureDuration.ToString());
                return cameraLastExposureDuration;
            }
        }

        public string LastExposureStartTime
        {
            get
            {
                if (!cameraImageReady)
                {
                    tl.LogMessage("LastExposureStartTime Get", "Throwing InvalidOperationException because of a call to LastExposureStartTime before the first image has been taken!");
                    throw new ASCOM.InvalidOperationException("Call to LastExposureStartTime before the first image has been taken!");
                }
                string exposureStartString = exposureStartTime.ToString("yyyy-MM-ddTHH:mm:ss");
                tl.LogMessage("LastExposureStartTime Get", exposureStartString.ToString());
                return exposureStartString;
            }
        }

        public int MaxADU
        {
            get
            {
                tl.LogMessage("MaxADU Get", "20000");
                return 20000;
            }
        }

        public short MaxBinX
        {
            get
            {
                tl.LogMessage("MaxBinX Get", "1");
                return 1;
            }
        }

        public short MaxBinY
        {
            get
            {
                tl.LogMessage("MaxBinY Get", "1");
                return 1;
            }
        }

        public int NumX
        {
            get
            {
                tl.LogMessage("NumX Get", cameraNumX.ToString());
                return cameraNumX;
            }
            set
            {
                cameraNumX = value;
                tl.LogMessage("NumX set", value.ToString());
            }
        }

        public int NumY
        {
            get
            {
                tl.LogMessage("NumY Get", cameraNumY.ToString());
                return cameraNumY;
            }
            set
            {
                cameraNumY = value;
                tl.LogMessage("NumY set", value.ToString());
            }
        }

        public short PercentCompleted
        {
            get
            {
                switch (status)
                {
                    case CameraStates.cameraWaiting:
                    case CameraStates.cameraExposing:
                    case CameraStates.cameraReading:
                    case CameraStates.cameraDownload:
                        short pc = (short)(((DateTime.Now - exposureStartTime).TotalSeconds / exposureDuration) * 100);
                        return pc;
                    case CameraStates.cameraIdle:
                        return (short)(cameraImageReady ? 100 : 0);
                    default:
                        throw new ASCOM.InvalidOperationException("get PercentCompleted is not valid if the camera is not active");
                }
            }
        }
    
        public double PixelSizeX
        {
            get
            {
                tl.LogMessage("PixelSizeX Get", pixelSize.ToString());
                return pixelSize;
            }
        }

        public double PixelSizeY
        {
            get
            {
                tl.LogMessage("PixelSizeY Get", pixelSize.ToString());
                return pixelSize;
            }
        }

        public void PulseGuide(GuideDirections Direction, int Duration)
        {
            tl.LogMessage("PulseGuide", "Not implemented");
            throw new ASCOM.MethodNotImplementedException("PulseGuide");
        }

        public short ReadoutMode
        {
            get
            {
                tl.LogMessage("ReadoutMode Get", "Not implemented");
                throw new ASCOM.PropertyNotImplementedException("ReadoutMode", false);
            }
            set
            {
                tl.LogMessage("ReadoutMode Set", "Not implemented");
                throw new ASCOM.PropertyNotImplementedException("ReadoutMode", true);
            }
        }

        public ArrayList ReadoutModes
        {
            get
            {
                tl.LogMessage("ReadoutModes Get", "Not implemented");
                throw new ASCOM.PropertyNotImplementedException("ReadoutModes", false);
                // check the ASCOM specification
            }
        }

        public string SensorName
        {
            get
            {
                tl.LogMessage("SensorName Get", "cable release");
                return "cable release";
            }
        }

        public SensorType SensorType
        {
            get
            {
                return(SensorType.Monochrome);
            }
        }

        public double SetCCDTemperature
        {
            get
            {
                tl.LogMessage("SetCCDTemperature Get", "Not implemented");
                throw new ASCOM.PropertyNotImplementedException("SetCCDTemperature", false);
            }
            set
            {
                tl.LogMessage("SetCCDTemperature Set", "Not implemented");
                throw new ASCOM.PropertyNotImplementedException("SetCCDTemperature", true);
            }
        }
      
        public void StartExposure(double Duration, bool Light)            
        {            
            if (Duration < 0.0) throw new InvalidValueException("StartExposure", Duration.ToString(), "0.0 upwards");
            if (cameraNumX > ccdWidth) throw new InvalidValueException("StartExposure", cameraNumX.ToString(), ccdWidth.ToString());
            if (cameraNumY > ccdHeight) throw new InvalidValueException("StartExposure", cameraNumY.ToString(), ccdHeight.ToString());
            if (cameraStartX > ccdWidth) throw new InvalidValueException("StartExposure", cameraStartX.ToString(), ccdWidth.ToString());
            if (cameraStartY > ccdHeight) throw new InvalidValueException("StartExposure", cameraStartY.ToString(), ccdHeight.ToString());
            if (cameraStartX + cameraNumX > ccdWidth) throw new InvalidValueException("Frame position","offset", ccdWidth.ToString());
            if (cameraStartY + cameraNumY > ccdHeight) throw new InvalidValueException("Frame position", "offset", ccdHeight.ToString());
            if (connectedState)
            {
                cameraImageReady = false;
                imageArray = new int[ccdWidth, ccdHeight];
                if (exposureTimer == null)
                {
                    exposureTimer = new System.Timers.Timer();
                    exposureTimer.Elapsed += EndExposure;                    
                }
                exposureTimer.Interval = Math.Max((int)(Duration * 1000),1000);  // minimum 1 second
                status = CameraStates.cameraExposing;
                SwitchOn();
                exposureStartTime = DateTime.Now;
                exposureDuration = Duration;
                exposureTimer.Enabled = true;
                tl.LogMessage("StartExposure", Duration.ToString() + DateTime.Now.ToString() + Light.ToString());             
            }
        }

        // routine that concludes exposure based on timer event and fill array
        private void EndExposure(object sender, ElapsedEventArgs e)
        {
            exposureTimer.Enabled = false;
            SwitchOff();  // close relay and stop exposure
            cameraLastExposureDuration = (DateTime.Now - exposureStartTime).TotalSeconds;
            status = CameraStates.cameraDownload;
            FillImageArray();
            status = CameraStates.cameraIdle;
            cameraImageReady = true;
            tl.LogMessage("ExposureTimer_Elapsed", "done");
        }

        // converts image array into correct array format
        private void FillImageArray()
        {
            for (int y = 0; y < cameraNumY; y++)
            {
                for (int x = 0; x < cameraNumY; x++)
                {
                    imageArray[x, y] = (int) imageData[x, y, 0];
                }
            }
        }

// method to load jpeg from //Camera document subfolder and put it into array
        private void ReadImageFile()
        {
            imageData = new float[ccdWidth, ccdHeight, 1];
            try
            {
                bmp = (Bitmap)Image.FromFile(imagePath);
                int w = ccdWidth;
                int h = ccdHeight;
                for (int y = 0; y < h; y += 1)
                {
                    for (int x = 0; x < w; x += 1)
                    {
                        imageData[x, y, 0] = (bmp.GetPixel(x, y).GetBrightness() * 255);
                    }
                }
            }
            catch
            {
            }
        }

        public int StartX
        {
            get
            {
                tl.LogMessage("StartX Get", cameraStartX.ToString());
                return cameraStartX;
            }
            set
            {
                cameraStartX = value;
                tl.LogMessage("StartX Set", value.ToString());
            }
        }

        public int StartY
        {
            get
            {
                tl.LogMessage("StartY Get", cameraStartY.ToString());
                return cameraStartY;
            }
            set
            {
                cameraStartY = value;
                tl.LogMessage("StartY set", value.ToString());
            }
        }

        public void StopExposure()
        {
            if (connectedState)
            {
                switch (status)
                {
                    case CameraStates.cameraWaiting:
                    case CameraStates.cameraExposing:
                    case CameraStates.cameraReading:
                    case CameraStates.cameraDownload:
                        // these are all possible exposure states so we can stop the exposure
                        exposureTimer.Enabled = false;
                        cameraLastExposureDuration = (DateTime.Now - exposureStartTime).TotalSeconds;
                        FillImageArray();
                        status = CameraStates.cameraIdle;
                        cameraImageReady = true;
                        SwitchOff();
                        tl.LogMessage("stop exposure", "stopping");
                        break;
                    case CameraStates.cameraIdle:
                        break;
                    case CameraStates.cameraError:
                    default:
                        tl.LogMessage("StopExposure", "Not exposing");
                        // these states are where it isn't possible to stop an exposure
                        throw new ASCOM.InvalidOperationException("StopExposure not possible if not exposing");                        
                }             
            }
        }

        #endregion

        #region Private properties and methods
        // here are some useful properties and methods that can be used as required
        // to help with driver development

        #region ASCOM Registration

        // Register or unregister driver for ASCOM. This is harmless if already
        // registered or unregistered. 
        //
        /// <summary>
        /// Register or unregister the driver with the ASCOM Platform.
        /// This is harmless if the driver is already registered/unregistered.
        /// </summary>
        /// <param name="bRegister">If <c>true</c>, registers the driver, otherwise unregisters it.</param>
        private static void RegUnregASCOM(bool bRegister)
        {
            using (var P = new ASCOM.Utilities.Profile())
            {
                P.DeviceType = "Camera";
                if (bRegister)
                {
                    P.Register(driverID, driverDescription);
                }
                else
                {
                    P.Unregister(driverID);
                }
            }
        }

        /// <summary>
        /// This function registers the driver with the ASCOM Chooser and
        /// is called automatically whenever this class is registered for COM Interop.
        /// </summary>
        /// <param name="t">Type of the class being registered, not used.</param>
        /// <remarks>
        /// This method typically runs in two distinct situations:
        /// <list type="numbered">
        /// <item>
        /// In Visual Studio, when the project is successfully built.
        /// For this to work correctly, the option <c>Register for COM Interop</c>
        /// must be enabled in the project settings.
        /// </item>
        /// <item>During setup, when the installer registers the assembly for COM Interop.</item>
        /// </list>
        /// This technique should mean that it is never necessary to manually register a driver with ASCOM.
        /// </remarks>
        [ComRegisterFunction]
        public static void RegisterASCOM(Type t)
        {
            RegUnregASCOM(true);
        }

        /// <summary>
        /// This function unregisters the driver from the ASCOM Chooser and
        /// is called automatically whenever this class is unregistered from COM Interop.
        /// </summary>
        /// <param name="t">Type of the class being registered, not used.</param>
        /// <remarks>
        /// This method typically runs in two distinct situations:
        /// <list type="numbered">
        /// <item>
        /// In Visual Studio, when the project is cleaned or prior to rebuilding.
        /// For this to work correctly, the option <c>Register for COM Interop</c>
        /// must be enabled in the project settings.
        /// </item>
        /// <item>During uninstall, when the installer unregisters the assembly from COM Interop.</item>
        /// </list>
        /// This technique should mean that it is never necessary to manually unregister a driver from ASCOM.
        /// </remarks>
        [ComUnregisterFunction]
        public static void UnregisterASCOM(Type t)
        {
            RegUnregASCOM(false);
        }

        #endregion

        /// <summary>
        /// Returns true if there is a valid connection to the driver hardware
        /// </summary>
        private bool IsConnected
        {
            get // kmtronic
            {
                Serial.DiscardInBuffer(); // clear garbage
                byte[] relaycmd = new byte[4];
                relaycmd[0] = startByte;
                relaycmd[1] = relaynumber;  // relay 1 (only 1)
                relaycmd[2] = readpin;  // byte for setting
                //relaycmd[4] = something;
                Serial.Write(relaycmd, 0, txstringlen); // send read relay command
                for (int i = 0; i < rxstringlen; i++)  // read in rxstringlen  bytes  FF 01 xx yy
                {
                    relaystatus[i] = (byte)Serial.ReadByte();
                }
                return (relaystatus[1] == 1);  // you may need to check this is valid for your system
            }
        }

        /// <summary>
        /// Use this function to throw an exception if we aren't connected to the hardware
        /// </summary>
        /// <param name="message"></param>
        private void CheckConnected(string message)
        {
            if (!IsConnected)
            {
                throw new ASCOM.NotConnectedException(message);
            }
        }

        /// <summary>
        /// Read the device configuration from the ASCOM Profile store
        /// </summary>
        internal void ReadProfile()
        {
            using (Profile driverProfile = new Profile())
            {
                driverProfile.DeviceType = "Camera";
                tl.Enabled = Convert.ToBoolean(driverProfile.GetValue(driverID, traceStateProfileName, string.Empty, traceStateDefault));
                comPort = driverProfile.GetValue(driverID, comPortProfileName, string.Empty, comPortDefault);
                pixelSize = double.Parse(driverProfile.GetValue(driverID, "pixel size", string.Empty, pixelsizeDefault));
                ccdWidth = int.Parse(driverProfile.GetValue(driverID, "width", string.Empty, pixelwidthDefault));
                ccdHeight = int.Parse(driverProfile.GetValue(driverID, "height", string.Empty, pixelheightDefault));
            }
        }

        /// <summary>
        /// Write the device configuration to the  ASCOM  Profile store
        /// </summary>
        internal void WriteProfile()
        {
            using (Profile driverProfile = new Profile())
            {
                driverProfile.DeviceType = "Camera";
                driverProfile.WriteValue(driverID, traceStateProfileName, tl.Enabled.ToString());
                driverProfile.WriteValue(driverID, comPortProfileName, comPort.ToString());
                driverProfile.WriteValue(driverID, "pixel size", pixelSize.ToString());
                driverProfile.WriteValue(driverID, "width", ccdWidth.ToString());
                driverProfile.WriteValue(driverID, "height", ccdHeight.ToString());
            }
        }

        /// <summary>
        /// Log helper function that takes formatted strings and arguments
        /// </summary>
        /// <param name="identifier"></param>
        /// <param name="message"></param>
        /// <param name="args"></param>
        internal static void LogMessage(string identifier, string message, params object[] args)
        {
            var msg = string.Format(message, args);
            tl.LogMessage(identifier, msg);
        }
        #endregion
    }
}
