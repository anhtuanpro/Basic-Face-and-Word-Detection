using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;
using Windows.Media.Ocr;
using Windows.Media.SpeechSynthesis;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Xaml.Shapes;

// The Blank Page item template is documented at http://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace WordsAndFacesDetection
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        /// <summary>
        /// Brush for drawing the bounding box around each detected face.
        /// </summary>
        private readonly SolidColorBrush lineBrush = new SolidColorBrush(Windows.UI.Colors.Yellow);

        /// <summary>
        /// Thickness of the face bounding box lines.
        /// </summary>
        private readonly double lineThickness = 2.0;

        /// <summary>
        /// Transparent fill for the bounding box.
        /// </summary>
        private readonly SolidColorBrush fillBrush = new SolidColorBrush(Windows.UI.Colors.Transparent);

        /// <summary>
        /// Limit on the height of the source image (in pixels) passed into FaceDetector for performance considerations.
        /// Images larger that this size will be downscaled proportionally.
        /// </summary>
        /// <remarks>
        /// This is an arbitrary value that was chosen for this scenario, in which FaceDetector performance isn't too important but face
        /// detection accuracy is; a generous size is used.
        /// Your application may have different performance and accuracy needs and you'll need to decide how best to control input.
        /// </remarks>
        private readonly uint sourceImageHeightLimit = 1280;
        // Bitmap holder of currently loaded image.
        private SoftwareBitmap bitmap;
        // Bitmap decoder of currently loaed image
        private BitmapDecoder decoder;
        // Writeable Bitmap
        private WriteableBitmap imgSource;
        // Recognized words overlay boxes.
        private List<WordOverlay> wordBoxes = new List<WordOverlay>();
        private String speakmessage;
        private SpeechSynthesizer synthesizer;
        public MainPage()
        {
            this.InitializeComponent();
            synthesizer = new SpeechSynthesizer();
        }

        /// <summary>
        /// Updates any existing face bounding boxes in response to changes in the size of the Canvas.
        /// </summary>
        /// <param name="sender">Canvas object whose size has changed</param>
        /// <param name="e">Event data</param>
        private void PhotoCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            try
            {
                //// Update word overlay boxes.
                if(wordBoxes.Count > 0)
                {
                    UpdateWordBoxTransform();
                }
                //// Update image rotation center.
                var rotate = TextOverlay.RenderTransform as RotateTransform;
                if (rotate != null)
                {
                    rotate.CenterX = PhotoCanvas.ActualWidth / 2;
                    rotate.CenterY = PhotoCanvas.ActualHeight / 2;
                }
                // If the Canvas is resized we must recompute a new scaling factor and
                // apply it to each face box.
                if (this.PhotoCanvas.Background != null)
                {
                    WriteableBitmap displaySource = (this.PhotoCanvas.Background as ImageBrush).ImageSource as WriteableBitmap;

                    double widthScale = displaySource.PixelWidth / this.PhotoCanvas.ActualWidth;
                    double heightScale = displaySource.PixelHeight / this.PhotoCanvas.ActualHeight;

                    foreach (var item in PhotoCanvas.Children)
                    {
                        Rectangle box = item as Rectangle;
                        if (box == null)
                        {
                            continue;
                        }

                        // We saved the original size of the face box in the rectangles Tag field.
                        BitmapBounds faceBounds = (BitmapBounds)box.Tag;
                        box.Width = (uint)(faceBounds.Width / widthScale);
                        box.Height = (uint)(faceBounds.Height / heightScale);

                        box.Margin = new Thickness((uint)(faceBounds.X / widthScale), (uint)(faceBounds.Y / heightScale), 0, 0);
                    }
                }
            }
            catch (Exception ex)
            {
                StatusBlock.Text = ex.ToString();
            }
        }
        /// <summary>
        /// Clears the display of image and face boxes.
        /// </summary>
        private void ClearVisualization()
        {
            this.PhotoCanvas.Background = null;
            this.PhotoCanvas.Children.Clear();
            TextOverlay.RenderTransform = null;
            TextOverlay.Children.Clear();
            wordBoxes.Clear();
            StatusBlock.Text = "";
        }

        private async void LoadImage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                FileOpenPicker photoPicker = new FileOpenPicker();
                photoPicker.ViewMode = PickerViewMode.Thumbnail;
                photoPicker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
                photoPicker.FileTypeFilter.Add(".jpg");
                photoPicker.FileTypeFilter.Add(".jpeg");
                photoPicker.FileTypeFilter.Add(".png");
                photoPicker.FileTypeFilter.Add(".bmp");

                StorageFile photoFile = await photoPicker.PickSingleFileAsync();
                if (photoFile == null)
                {
                    return;
                }

                this.ClearVisualization();
                this.StatusBlock.Text = "Opening...";
                await LoadFileImage(photoFile);
                this.StatusBlock.Text = "Image is loaded";
            }
            catch (Exception ex)
            {
                this.ClearVisualization();
                this.StatusBlock.Text = ex.ToString();
            }
        }
        /// <summary>
        /// Takes the photo image and FaceDetector results and assembles the visualization onto the Canvas.
        /// </summary>
        /// <param name="displaySource">Bitmap object holding the image we're going to display</param>
        /// <param name="foundFaces">List of detected faces; output from FaceDetector</param>
        private void SetupVisualization(WriteableBitmap displaySource, IList<DetectedFace> foundFaces)
        {
            ImageBrush brush = new ImageBrush();
            brush.ImageSource = displaySource;
            brush.Stretch = Stretch.Fill;
            this.PhotoCanvas.Background = brush;

            if (foundFaces != null)
            {
                double widthScale = displaySource.PixelWidth / this.PhotoCanvas.ActualWidth;
                double heightScale = displaySource.PixelHeight / this.PhotoCanvas.ActualHeight;

                foreach (DetectedFace face in foundFaces)
                {
                    // Create a rectangle element for displaying the face box but since we're using a Canvas
                    // we must scale the rectangles according to the image’s actual size.
                    // The original FaceBox values are saved in the Rectangle's Tag field so we can update the
                    // boxes when the Canvas is resized.
                    Rectangle box = new Rectangle();
                    box.Tag = face.FaceBox;
                    box.Width = (uint)(face.FaceBox.Width / widthScale);
                    box.Height = (uint)(face.FaceBox.Height / heightScale);
                    box.Fill = this.fillBrush;
                    box.Stroke = this.lineBrush;
                    box.StrokeThickness = this.lineThickness;
                    box.Margin = new Thickness((uint)(face.FaceBox.X / widthScale), (uint)(face.FaceBox.Y / heightScale), 0, 0);

                    this.PhotoCanvas.Children.Add(box);
                }
            }

            string message;
            if (foundFaces == null || foundFaces.Count == 0)
            {
                message = "Didn't find any human faces in the image";
            }
            else if (foundFaces.Count == 1)
            {
                message = "Found a human face in the image";
            }
            else
            {
                message = "Found " + foundFaces.Count + " human faces in the image";
            }

            this.StatusBlock.Text=message;
            speakmessage = message;
            //Speak(message);
        }

        /// <summary>
        /// Computes a BitmapTransform to downscale the source image if it's too large. 
        /// </summary>
        /// <remarks>
        /// Performance of the FaceDetector degrades significantly with large images, and in most cases it's best to downscale
        /// the source bitmaps if they're too large before passing them into FaceDetector. Remember through, your application's performance needs will vary.
        /// </remarks>
        /// <param name="sourceDecoder">Source image decoder</param>
        /// <returns>A BitmapTransform object holding scaling values if source image is too large</returns>
        private BitmapTransform ComputeScalingTransformForSourceImage(BitmapDecoder sourceDecoder)
        {
            BitmapTransform transform = new BitmapTransform();

            if (sourceDecoder.PixelHeight > this.sourceImageHeightLimit)
            {
                float scalingFactor = (float)this.sourceImageHeightLimit / (float)sourceDecoder.PixelHeight;

                transform.ScaledWidth = (uint)Math.Floor(sourceDecoder.PixelWidth * scalingFactor);
                transform.ScaledHeight = (uint)Math.Floor(sourceDecoder.PixelHeight * scalingFactor);
            }

            return transform;
        }

        private async Task<IList<DetectedFace>> DetectFaces(SoftwareBitmap originalBitmap)
        {
            IList<DetectedFace> faces = null;
            SoftwareBitmap detectorInput = null;
            // We need to convert the image into a format that's compatible with FaceDetector.
            // Gray8 should be a good type but verify it against FaceDetector’s supported formats.
            const BitmapPixelFormat InputPixelFormat = BitmapPixelFormat.Gray8;
            if (FaceDetector.IsBitmapPixelFormatSupported(InputPixelFormat))
            {
                using (detectorInput = SoftwareBitmap.Convert(originalBitmap, InputPixelFormat))
                {
                    // Create a WritableBitmap for our visualization display; copy the original bitmap pixels to wb's buffer.
                   // displaySource = new WriteableBitmap(originalBitmap.PixelWidth, originalBitmap.PixelHeight);
                   // originalBitmap.CopyToBuffer(displaySource.PixelBuffer);

                    this.StatusBlock.Text = "Detecting...";

                    // Initialize our FaceDetector and execute it against our input image.
                    // NOTE: FaceDetector initialization can take a long time, and in most cases
                    // you should create a member variable and reuse the object.
                    // However, for simplicity in this scenario we instantiate a new instance each time.
                    FaceDetector detector = await FaceDetector.CreateAsync();
                    faces = await detector.DetectFacesAsync(detectorInput);

                    // Create our display using the available image and face results.
                    // this.SetupVisualization(displaySource, faces);
                }
            }
            else
            {
                StatusBlock.Text = "PixelFormat '" + InputPixelFormat.ToString() + "' is not supported by FaceDetector";
            }
            return faces;
        }

        //detect words method
        private async Task DetectWords(SoftwareBitmap bitmap)
        {
            //List<WordOverlay> words = new List<WordOverlay>();
            // Check if OcrEngine supports image resoulution.
            if (bitmap.PixelWidth > OcrEngine.MaxImageDimension || bitmap.PixelHeight > OcrEngine.MaxImageDimension)
            {

                String message = String.Format("Bitmap dimensions ({0}x{1}) are too big for OCR.", bitmap.PixelWidth, bitmap.PixelHeight) +
                    "Max image dimension is " + OcrEngine.MaxImageDimension + ".";
                StatusBlock.Text += Environment.NewLine+ message;
                return;
            }

            OcrEngine ocrEngine = null;
            ocrEngine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en"));
            if (ocrEngine != null)
            {
                // Recognize text from image.
                var ocrResult = await ocrEngine.RecognizeAsync(bitmap);

                if (ocrResult.TextAngle != null)
                {
                    // If text is detected under some angle in this sample scenario we want to
                    // overlay word boxes over original image, so we rotate overlay boxes.
                    TextOverlay.RenderTransform = new RotateTransform
                    {
                        Angle = (double)ocrResult.TextAngle,
                        CenterX = PhotoCanvas.ActualWidth / 2,
                        CenterY = PhotoCanvas.ActualHeight / 2
                    };
                }
                if(ocrResult.Text.Count()> 0)
                {
                    speakmessage += ". The recognized words are " + ocrResult.Text;
                    // Display recognized text.
                    StatusBlock.Text += Environment.NewLine + ocrResult.Text;
                }
                else
                {
                    speakmessage += ". No words founded";
                    // Display recognized text.
                    StatusBlock.Text += Environment.NewLine + "No words founded";
                }
                
                //Speak(ocrResult.Text);
                // Create overlay boxes over recognized words.
                foreach (var line in ocrResult.Lines)
                {
                    Rect lineRect = Rect.Empty;
                    foreach (var word in line.Words)
                    {
                        lineRect.Union(word.BoundingRect);
                    }

                    // Determine if line is horizontal or vertical.
                    // Vertical lines are supported only in Chinese Traditional and Japanese languages.
                    bool isVerticalLine = lineRect.Height > lineRect.Width;

                    foreach (var word in line.Words)
                    {
                        WordOverlay wordBoxOverlay = new WordOverlay(word);

                        // Keep references to word boxes.
                        wordBoxes.Add(wordBoxOverlay);

                        // Define overlay style.
                        var overlay = new Border()
                        {
                            Style = isVerticalLine ?
                                        (Style)this.Resources["HighlightedWordBoxVerticalLine"] :
                                        (Style)this.Resources["HighlightedWordBoxHorizontalLine"]
                        };

                        // Bind word boxes to UI.
                        overlay.SetBinding(Border.MarginProperty, wordBoxOverlay.CreateWordPositionBinding());
                        overlay.SetBinding(Border.WidthProperty, wordBoxOverlay.CreateWordWidthBinding());
                        overlay.SetBinding(Border.HeightProperty, wordBoxOverlay.CreateWordHeightBinding());

                        // Put the filled textblock in the results grid.
                        TextOverlay.Children.Add(overlay);
                    }
                }

                // Rescale word boxes to match current UI size.
                UpdateWordBoxTransform();
            }
            //return words;
        }
        private async void DetectImage_Click(object sender, RoutedEventArgs e)
        {
            // detect faces
            IList<DetectedFace> faces = null;
            //SoftwareBitmap detectorInput = null;
            //WriteableBitmap displaySource = null;
            faces = await DetectFaces(bitmap);
            SetupVisualization(imgSource, faces);
            await DetectWords(bitmap);
            Speak(speakmessage);
        }

        /// <summary>
        /// Loads image from file to bitmap and displays it in UI.
        /// </summary>
        /// <param name="file"></param>
        /// <returns></returns>
        private async Task LoadFileImage(StorageFile file)
        {
            using (var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read))
            {
                decoder = await BitmapDecoder.CreateAsync(stream);
                BitmapTransform transform = ComputeScalingTransformForSourceImage(decoder);
                bitmap = await decoder.GetSoftwareBitmapAsync(decoder.BitmapPixelFormat, BitmapAlphaMode.Ignore, transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
                imgSource = new WriteableBitmap(bitmap.PixelWidth, bitmap.PixelHeight);
                bitmap.CopyToBuffer(imgSource.PixelBuffer);
                ImageBrush brush = new ImageBrush();
                brush.ImageSource = imgSource;
                brush.Stretch = Stretch.Fill;
                PhotoCanvas.Width = Window.Current.Bounds.Width;
                //update photo canvas height following the scale of bitmap width and height
                PhotoCanvas.Height = bitmap.PixelHeight * PhotoCanvas.Width / bitmap.PixelWidth;
                this.PhotoCanvas.Background = brush;
                DetectImage.IsEnabled = true;
            }
        }

        /// <summary>
        ///  Update word box transform to match current UI size.
        /// </summary>
        private void UpdateWordBoxTransform()
        {
            // Used for text overlay.
            // Prepare scale transform for words since image is not displayed in original size.
            ScaleTransform scaleTrasform = new ScaleTransform
            {
                CenterX = 0,
                CenterY = 0,
                ScaleX = PhotoCanvas.ActualWidth / bitmap.PixelWidth,
                ScaleY = PhotoCanvas.ActualHeight / bitmap.PixelHeight
            };

            foreach (var item in wordBoxes)
            {
                item.Transform(scaleTrasform);
            }
        }
        /// <summary>
        /// 
        /// 
        /// </summary>
        /// <param name="text"></param>
        private async void Speak(string text)
        {
            if (media.CurrentState.Equals(MediaElementState.Playing))
            {
                media.Stop();
            }
            else
            {
                if (!String.IsNullOrEmpty(text))
                {
                    try
                    {
                        SpeechSynthesisStream synthesisStream = await synthesizer.SynthesizeTextToStreamAsync(text);

                        // Set the source and start playing the synthesized 
                        // audio stream.
                        media.AutoPlay = true;
                        media.SetSource(synthesisStream, synthesisStream.ContentType);
                        media.Play();
                    }
                    catch (System.IO.FileNotFoundException)
                    {
                        // If media player components are unavailable, 
                        // (eg, using a N SKU of windows), we won't be able 
                        // to start media playback. Handle this gracefully 
                        var messageDialog = new Windows.UI.Popups.MessageDialog("Media player components unavailable");
                        await messageDialog.ShowAsync();
                    }
                    catch (Exception)
                    {
                        // If the text is unable to be synthesized, throw 
                        // an error message to the user.
                        media.AutoPlay = false;
                        var messageDialog = new Windows.UI.Popups.MessageDialog("Unable to synthesize text");
                        await messageDialog.ShowAsync();
                    }
                }
            }
        }
    }
}
