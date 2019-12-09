using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO.Ports;

// References: http://www.emgu.com/wiki/index.php/Shape_(Triangle,_Rectangle,_Circle,_Line)_Detection_in_CSharp#Source_Code

namespace Lab_5
{
    public partial class Form1 : Form
    {
        // Set up camera
        VideoCapture _capture;
        Thread _captureThread;
        int count = 0;  // counts how many frames the camera takes in

        // Set up serial communication
        SerialPort arduinoSerial = new SerialPort();
        bool enableCoordinateSending = true;
        Thread serialMonitoringThread;
        public Form1()
        {
            InitializeComponent();
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            // create the capture object and processing thread
            _capture = new VideoCapture(1);
            _captureThread = new Thread(ProcessImage);
            _captureThread.Start();

            // Open the serial port and send an error message if it does not work
            try
            {
                arduinoSerial.PortName = "COM11";
                arduinoSerial.BaudRate = 9600;
                arduinoSerial.Open();
                serialMonitoringThread = new Thread(MonitorSerialData);
                serialMonitoringThread.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error Initializing COM port");
                Close();
            }
        }

        private void ProcessImage()
        {
            while (_capture.IsOpened)
            {
                Mat workingImage = _capture.QueryFrame();
                // resize to PictureBox aspect ratio
                int newHeight = (workingImage.Size.Height * sourcePictureBox.Size.Width) / workingImage.Size.Width;
                Size newSize = new Size(sourcePictureBox.Size.Width, newHeight);
                CvInvoke.Resize(workingImage, workingImage, newSize);

                // Flip the image so that the origin is at the bottom left corner of the paper
                // if needed, code for vertical flip: CvInvoke.Flip(workingImage, workingImage, FlipType.Vertical);
                CvInvoke.Flip(workingImage, workingImage, FlipType.Horizontal);

                // as a test for comparison, create a copy of the image with a binary filter:
                var binaryImage = workingImage.ToImage<Gray, byte>().ThresholdBinary(new Gray(125), new
                Gray(255)).Mat;

                Image<Gray, byte> grayImg = workingImage.ToImage<Gray, byte>().ThresholdBinary(new Gray(120), new Gray(255));

                // Sample for gaussian blur:
                var blurredImage = new Mat();
                var cannyImage = new Mat();
                var decoratedImage = new Mat();
                CvInvoke.GaussianBlur(workingImage, blurredImage, new Size(11, 11), 0);
                // convert to B/W
                CvInvoke.CvtColor(blurredImage, blurredImage, typeof(Bgr), typeof(Gray));
                // apply canny:
                // NOTE: Canny function can frequently create duplicate lines on the same shape
                // depending on blur amount and threshold values, some tweaking might be needed.
                // You might also find that not using Canny and instead using FindContours on
                // a binary-threshold image is more accurate.
                CvInvoke.Canny(blurredImage, cannyImage, 150, 255);
                // make a copy of the canny image, convert it to color for decorating:
                CvInvoke.CvtColor(cannyImage, decoratedImage, typeof(Gray), typeof(Bgr));

                List<ShapeDto> shapesFound = new List<ShapeDto>();
                // find contours:
                using (VectorOfVectorOfPoint contours = new VectorOfVectorOfPoint())
                {
                    // Build list of contours
                    CvInvoke.FindContours(grayImg, contours, null, RetrType.List, ChainApproxMethod.ChainApproxSimple);
                    for (int i = 0; i < contours.Size; i++)
                    {
                        Rectangle boundingBox = CvInvoke.BoundingRectangle(contours[i]);
                        Point center = new Point(boundingBox.X + boundingBox.Width / 2, boundingBox.Y + boundingBox.Height / 2);

                        using (VectorOfPoint contour = contours[i])
                        using (VectorOfPoint approxContour = new VectorOfPoint())
                        {
                            CvInvoke.ApproxPolyDP(contour, approxContour, CvInvoke.ArcLength(contour, true) * 0.05, true);
                            CvInvoke.Polylines(decoratedImage, contour, true, new Bgr(Color.Green).MCvScalar);

                            //only consider contours with area greater than 300 and less that 12000
                            if (CvInvoke.ContourArea(approxContour, false) > 300 && CvInvoke.ContourArea(approxContour, false) < 12000) 
                            {
                                if (approxContour.Size == 3) //The contour has 3 vertices, it is a triangle
                                {
                                    shapesFound.Add(new ShapeDto()
                                    {
                                        ShapeName = "Triangle",                                        
                                        LocX = center.X,
                                        LocY = center.Y
                                    });
                                }
                                else if (approxContour.Size == 4) //The contour has 4 vertices, it is a square
                                {
                                    shapesFound.Add(new ShapeDto()
                                    {
                                        ShapeName = "Square",
                                        LocX = center.X,
                                        LocY = center.Y
                                    });
                                }
                                else
                                    continue;

                                // Finding the area of the contour
                                double area = CvInvoke.ContourArea(contour);
                                // Draw on the display frame only if the item detected has an area smaller than half that of the frame 
                                if (boundingBox.Width * boundingBox.Height < 0.5 * decoratedImage.Height * decoratedImage.Width)
                                {
                                    MarkDetectedObject(workingImage, contours[i], boundingBox, area, shapesFound.Last().ShapeName);
                                    Invoke(new Action(() =>
                                    {
                                        label3.Text = $"{shapesFound.Last().LocX}, {shapesFound.Last().LocY}";
                                        label4.Text = $"Position: {center.X}, {center.Y}";
                                    }));
                                }
                            }
                        }
                        Invoke(new Action(() =>
                        {
                            label1.Text = $"There are {contours.Size} contours detected";
                            label2.Text = $"There are {shapesFound.Count} shapes detected";
                            //label1.Text = $"Width: {workingImage.Width}";
                            //label2.Text = $"Height {workingImage.Height}";
                        }));
                    }
                }
                
                // output images:
                sourcePictureBox.Image = workingImage.Bitmap;
                contourPictureBox.Image = decoratedImage.Bitmap;

                // once the frame count is greater than 50, start sending data to the arduino
                // this provides a buffer while the camera sets up
                count++;
                if (count > 50 && shapesFound.Count > 0)
                {
                    sendData(shapesFound, workingImage.Width, workingImage.Height);
                    Thread.Sleep(20000); // wait 20 seconds for the arduino to perform the operation
                }
            }

        }

        private static void MarkDetectedObject(Mat frame, VectorOfPoint contour, Rectangle boundingBox, double area, string shape)
        {
            // Drawing contour and box around it
            if (shape == "Square")
            CvInvoke.Polylines(frame, contour, true, new Bgr(Color.Red).MCvScalar);
            if (shape == "Triangle")
            CvInvoke.Polylines(frame, contour, true, new Bgr(Color.Green).MCvScalar);

            CvInvoke.Rectangle(frame, boundingBox, new Bgr(Color.Blue).MCvScalar);
            // Write information next to marked object
            Point center = new Point(boundingBox.X + boundingBox.Width / 2, boundingBox.Y + boundingBox.Height / 2);
            double areaRatio = area / (boundingBox.Height * boundingBox.Width) * 100;
            
            var info = new string[] {
            $"Area: {area}",
            $"Position: {center.X}, {center.Y}",
            $"Shape: {shape}"
            };
            CvInvoke.Circle(frame, center, 5, new Bgr(Color.Blue).MCvScalar, 5);

            WriteMultilineText(frame, info, new Point(center.X, boundingBox.Bottom + 12));
        }

        private static void WriteMultilineText(Mat frame, string[] lines, Point origin)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                int y = i * 10 + origin.Y; // Moving down on each line
                CvInvoke.PutText(frame, lines[i], new Point(origin.X, y),
                FontFace.HersheyPlain, 0.8, new Bgr(Color.Red).MCvScalar);
            }
        }

        private void MonitorSerialData()
        {
            while (true)
            {
                // block until \n character is received, extract command data
                string msg = arduinoSerial.ReadLine();
                // confirm the string has both < and > characters
                if (msg.IndexOf("<") == -1 || msg.IndexOf(">") == -1)
                {
                    continue;
                }
                // remove everything before the < character
                msg = msg.Substring(msg.IndexOf("<") + 1);
                // remove everything after the > character
                msg = msg.Remove(msg.IndexOf(">"));
                // if the resulting string is empty, disregard and move on
                if (msg.Length == 0)
                {
                    continue;
                }
                // parse the command
                if (msg.Substring(0, 1) == "S")
                {
                    // command is to suspend, toggle states accordingly:
                    ToggleFieldAvailability(msg.Substring(1, 1) == "1");
                }
                else if (msg.Substring(0, 1) == "P")
                {
                    // command is to display the point data, output to the text field:
                    Invoke(new Action(() =>
                    {
                        returnedPointLbl.Text = $"Returned Point Data: {msg.Substring(1)}";
                    }));
                }
            }
        }
        private void ToggleFieldAvailability(bool suspend)
        {
            // Suspends the serial communication if the arduino isn't ready to be sent data
            Invoke(new Action(() =>
            {
                enableCoordinateSending = !suspend;
                lockStateToolStripStatusLabel.Text = $"State: {(suspend ? "Locked" : "Unlocked")}";
            }));
        }

        private void sendData(List<ShapeDto> shapesFound, int width, int height)
        {
            if (!enableCoordinateSending)
            {
                MessageBox.Show("Temporarily locked...");
                return;
            }

            int shape = 0;
            double[] angles = new double[3];    // to store the angles values for each servo
            angles = MathCalculations(shapesFound.Last().LocX, shapesFound.Last().LocY, width, height);

            // sends the arduino a 1 if the shape is a square and a 2 if it is a triangle
            if (shapesFound.Last().ShapeName == "Square")
            {
                shape = 1;
            }
            else if (shapesFound.Last().ShapeName == "Triangle")
            {
                shape = 2;
            }
            // sends data to the arduino
            byte[] buffer = new byte[6] {
                    Encoding.ASCII.GetBytes("<")[0],
                    Convert.ToByte(angles[0]),
                    Convert.ToByte(angles[1]),
                    Convert.ToByte(angles[2]),
                    Convert.ToByte(shape),
                    Encoding.ASCII.GetBytes(">")[0]
                    };
            arduinoSerial.Write(buffer, 0, 6);

        }

        private double[] MathCalculations(int centerX, int centerY, int width, int height)
        {
            // the z offset is 90 mm
            const double offsetZ = 90;
            // the y offset from paper is 255 mm
            const double offsetY = 255;
            // this is the array that stores the angle values to send to the arduino
            double[] angles = new double[3];
            // initialize theta values to the home position
            double theta1 = 60, theta2 = 30, theta3 = 40;
            double theta2prime, theta4; // angles to help find theta2

            //convert x and y from pixels to mm
            double x = (centerX * 13 * 25.4) / width;
            double y = ((centerY * 9 * 25.4) / height) + offsetY;
            double center = ((13 * 25.4) / 2);

            // find the distance from the center for x
            double distanceFromCenterX = Math.Abs(center - x);

            // Add offsets for the tool which is off center
            if (x <= center)
            {
                distanceFromCenterX = distanceFromCenterX + 15;
            }
            if (x > center)
            {
                distanceFromCenterX = distanceFromCenterX - 15;
            }

            // r is the distance from fromt the center of the robot to the part
            // r2 is r squared
            double r2 = y * y + distanceFromCenterX * distanceFromCenterX;
            double r = Math.Sqrt(r2);

            // Calculate theta1
            // originally, theta1 is the angle from the center of the page
            theta1 = (Math.Atan(distanceFromCenterX / y) * (180 / Math.PI));
            // factor to remove error
            if(theta1 > 3)
                theta1 = theta1 - (theta1 * 0.2);
            // since the home angle is 60, add or subtract to find the true theta1
            if (x < center)
            {
                theta1 = 60 + theta1;
            }
            if (x > center)
            {
                theta1 = 60 - theta1;
            }
            if (x == center)
            {
                theta1 = 61;
            }
            // if theta1 is 60 or 62, it will send to the arduino as one of the buffer characters < or >
            // so set these values equal to either 61 or 63
            if (Convert.ToInt32(theta1) == 60)
            {
                theta1 = 61;
            }
            if (Convert.ToInt32(theta1) == 62)
            {
                theta1 = 63;
            }

            // Calculate theta3 using the law of cosines
            theta3 = Math.Acos((300 * 300 + 250 * 250 - r2 - offsetZ * offsetZ) /
                    (2 * 300 * 250)) * (180 / Math.PI);
            // offset theta 3 from the true position
            theta3 = theta3 - 6;
            // if theta3 is 60 or 62, it will send to the arduino as one of the buffer characters < or >
            // so set these values equal to either 61 or 63
            if (Convert.ToInt32(theta3) == 60)
            {
                theta3 = 61;
            }
            if (Convert.ToInt32(theta3) == 62)
            {
                theta3 = 63;
            }

            // law of cosines for theta4
            theta4 = (Math.Acos((300 * 300 + offsetZ * offsetZ + r2 - 250 * 250) /
                    (2 * 300 * Math.Sqrt(offsetZ * offsetZ + r2)))) * (180 / Math.PI);
            // pythagorean theorem to find angle theta2prime
            theta2prime = Math.Atan(r / offsetZ) * (180 / Math.PI);
            // theta2 is the compliment of theta2prime plus theta4
            theta2 = 180 - (theta2prime + theta4);
            // offset from home
            theta2 = (theta2 * 1.1) + 18;
            // if theta1 is 60 or 62, it will send to the arduino as one of the buffer characters < or >
            // so set these values equal to either 61 or 63
            if (Convert.ToInt32(theta2) == 60)
            {
                theta2 =  61;
            }
            if (Convert.ToInt32(theta2) == 62)
            {
                theta2 = 63;
            }

            // print values to screen
            Invoke(new Action(() =>
            {
                label5.Text = $"Theta 1: {theta1}";
                label6.Text = $"Theta 2: {theta2}";
                label7.Text = $"Theta 3: {theta3}";
                label8.Text = $"Theta 4: {theta4}";
            }));
            // set the items in the array equal to the angles
            angles[0] = theta1;
            angles[1] = theta2;
            angles[2] = theta3;
            // return the array of angles
            return angles;
        }
        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            // terminate the image processing thread and serial monitoring thread to avoid orphaned processes
            _captureThread.Abort();
            serialMonitoringThread.Abort();
        }

    }
}
