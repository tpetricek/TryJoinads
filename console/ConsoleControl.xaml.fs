//----------------------------------------------------------------------------
//
// Copyright (c) 2002-2011 Microsoft Corporation. 
//
// This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
// copy of the license can be found in the License.html file at the root of this distribution. 
// By open this source code in any fashion, you are agreeing to be bound 
// by the terms of the Apache License, Version 2.0.
//
// You must not remove this notice, or any other, from this software.
//----------------------------------------------------------------------------

namespace FSharp.Console

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Threading
open System.Windows
open System.Windows.Browser
open System.Windows.Controls
open System.Windows.Controls.Primitives
open System.Windows.Documents
open System.Windows.Input
open System.Windows.Media
open Microsoft.FSharp.Compiler.Interactive

[<AutoOpen>]
module private Utilities = 

    /// Use this implementation of the dynamic binding operator
    /// to bind to Xaml components in code-behind, see example below
    let (?) (c:obj) (s:string) =
        match c with 
        | :? ResourceDictionary as r ->  r.[s] :?> 'T
        | :? Control as c -> c.FindName(s) :?> 'T
        | _ -> failwith "dynamic lookup failed"


/// Specifies the positioning of the canvas component within the Try F# control.
type CanvasPosition = 
    /// Canvas is the only visible component in the control's main pane.
    | Alone 
    /// Canvas is visible and positioned to the right in the control's main pane.
    | Right 
    /// Canvas is not visible.
    | Hidden

/// <summary>
/// Defines a composite control to interact with F# Interactive in Silverlight.
/// </summary>
/// <remarks>
/// The composite control is made up of a script editing window, a textual output window,
/// a graphical windows and a toolbar. The control embeds an instance of FSI, feeding it 
/// F# code from the editing text box and capturing output from FSI into the lower output
/// text box.
/// </remarks>
type ConsoleControl() as this = 
    inherit UserControl()
    do Application.LoadComponent(this, new System.Uri("/FSharp.Console;component/ConsoleControl.xaml", System.UriKind.Relative));
    let txtInput : RichTextBox = this?txtInput
    let txtOutput : TextBox = this?txtOutput
    let outputCanvas : Canvas = this?OutputCanvas
    let btnLoad : Button = this?btnLoad
    let btnSave : Button = this?btnSave
    let btnState1 : Button = this?btnState1
    let btnState2 : Button = this?btnState2
    let btnState3 : CheckBox = this?btnState3
    let splitter2 : GridSplitter = this?splitter2
    let canvasButtons : StackPanel = this?canvasButtons
    let btnGo : Button = this?btnGo
    let layoutRoot : Grid = this?LayoutRoot
    //// Number of spaces inserted when Tab key is pressed.
    let TabSpace = "    "
    //// Delay between a source change and activation of the syntax colorization process. 
    let ColorizationDelay = 100
    //// Delay injected between loops of the syntax colorization process.
    let ColorizationPause = 250
    //// Wait time injected in the loop which transfers output from FSI to the UI.
    let OutputPause = 50

    let WelcomeMessage = ""
                          + "// Welcome to Try Joinads!\n"
                          + "// Use Ctrl-Enter to run the selected text in F# Interactive. \n"

    //// TextReader to feed code to the compiler
    let mutable fsiIn : StreamReader = null
    //// TextWriter to capture output from FSI. Same writer is used for output and errors.
    let mutable fsiOut : StreamWriter = null
    //// Active instance of FSI.
    let mutable consoleOpt : Runner.InteractiveConsole option  = None
    //// Active source code services.
    let mutable srcCodeServices : Runner.SimpleSourceCodeServices  option = None
    //// Timer to fire colorization work on background thread
    let mutable colorizationTimer : Timer  = null
    //// The source code to colorize. Not null if colorization is on-going, null otherwise.
    let mutable sourceToColorize = null
    //// A True value indicates that the source in the editor has changed since the current
    //// colorization began.
    let mutable sourceToColorizeInvalidated = false

    /// Gets a value indicating whether or not the content of the script window has changed
    /// since it was last saved or loaded.
    let mutable isDirty = false

    /// <summary>
    /// Gets or sets the maximum number of lines that are echoed to the output window
    /// when a script is sent to F# Interactive for execution.
    /// </summary>
    /// <remarks>
    /// Set this property to zero or less to turn off echoing. The default value is 10.
    /// </remarks>
    let maxLinesToEcho =  10

    let createFsiReader() = new StreamReader(new CompilerInputStream())

    let createFsiWriter() =  new StreamWriter(new CompilerOutputStream(), AutoFlush = true)

    let txtInputHandler = ContentChangedEventHandler (fun _ _ ->
            isDirty <- true
            this.Colorize(ColorizationDelay))

    //#region Public and Scriptable API

    /// Gets or sets the content of the script window.
    [<ScriptableMember()>]
    member this.Script 
        with get() = RichTextTool.GetTextFromRichText(txtInput) 
        and set txt  =
            let txt = if String.IsNullOrEmpty(txt) then String.Empty else txt
 
            RichTextTool.SetTextToRichText(txtInput, txt)
            txtInput.Selection.Select(txtInput.ContentEnd, txtInput.ContentEnd)
            isDirty <- true


    /// <summary>
    /// Gets the content of the output window.
    /// </summary>
    member this.Output = txtOutput.Text 


    /// <summary>
    /// Gets a handle to the canvas instance defined in the main pane of the control.
    /// </summary>
    member this.Canvas = outputCanvas 

    /// <summary>
    /// Gets or sets the position of the canvas window.
    /// </summary>
    /// <param name="position">The positioning to apply.</param>
    member this.CanvasPosition 
        with get () = 
            if btnState3.IsChecked = Nullable true then 
                if btnState1.IsEnabled then 
                    CanvasPosition.Right
                else
                    CanvasPosition.Alone
            else
                CanvasPosition.Hidden

        and set value = 
            if (value <> this.CanvasPosition) then 
                this.SetCanvasPosition(value)

    /// Sets the font size
    [<ScriptableMember>]
    member this.SetFontSize(v) =
      txtInput.FontSize <- v
      txtOutput.FontSize <- v

    /// Clears the current content of the output window.
    [<ScriptableMember>]
    member this.ClearOutput() = 
        txtOutput.Text <- @"> "
        txtOutput.Select(2, 0)

    /// Clears the current content of the canvas window.
    [<ScriptableMember>]
    member this.ClearCanvas() = 
        outputCanvas.Children.Clear()
        outputCanvas.Background <- new SolidColorBrush(Colors.Transparent)

    /// Sends F# Interactive a request to cancel the current computation.
    [<ScriptableMember>]
    member this.Cancel() =
        consoleOpt.Value.Interrupt()

    /// <summary>
    /// Loads the specified code in the script window.
    /// </summary>
    /// <param name="script">The text of the script to load.</param>
    [<ScriptableMember>]
    member this.LoadFromString(script:string)  =
        RichTextTool.SetTextToRichText(txtInput, script)
        txtInput.Selection.Select(txtInput.ContentStart, txtInput.ContentStart)
        isDirty <- false

    /// <summary>
    /// Fetches a script file at the specified URL and loads its content in the script window.
    /// </summary>
    /// <param name="url">The URL of the script to load.</param>
    [<ScriptableMember>]
    member this.LoadFromUrl(url:string) =
        let client = new WebClient()
        let mutable uri = null
        let absolute = Uri.TryCreate(url, UriKind.Absolute, &uri)
        let ok = 
            if absolute then 
                let msg = String.Format("You are attempting to load a file from an external site:\n{0}.\n\nWould you like to proceed?", uri.ToString())
                let result = MessageBox.Show(msg, "Attempting to load a script", MessageBoxButton.OKCancel)
                (MessageBoxResult.Cancel <> result) 
            else
                uri <- new Uri(client.BaseAddress + "/" + url)
                true

        if ok then 
            client.DownloadStringCompleted.Add(fun e -> 
                if ((e <> null) && (e.Cancelled = false) && (e.Error = null)) then
                    this.LoadFromString(e.Result)
                    isDirty <- false
                elif (e.Error <> null) then
                    let msg = e.Error.Message
                    let msg = if (String.IsNullOrEmpty(msg)) && (e.Error.InnerException <> null) then e.Error.InnerException.Message else msg

                    let msg = if String.IsNullOrEmpty(msg) then "An unknown error occured." else msg

                    txtOutput.Text <- txtOutput.Text + "\n\n! " + msg + "\n")

            client.DownloadStringAsync(uri)
            isDirty <- false

    /// <summary>
    /// Loads the content of a script file in the script window.
    /// </summary>
    /// <remarks>
    /// This method will bring up the OpenFileDialog to allow the user to select the file to load.
    /// </remarks>
    [<ScriptableMember>]
    member this.LoadFromFile() =
        let ofd = new OpenFileDialog(Filter = "F# Source Files (*.fs)|*.fs|F# Script Files (*.fsx)|*.fsx|Text Files (*.txt)|*.txt|All Files (*.*)|*.*")
        let accepted = ofd.ShowDialog() 
        if (accepted = Nullable true) then 
            use fileIn = ofd.File.OpenText() 
            let contents = fileIn.ReadToEnd()
            this.LoadFromString(contents)

    /// <summary>
    /// Saves the content of the script window to a file.
    /// </summary>
    /// <remarks>
    /// This method will bring up the SaveFileDialog to allow the user to select the destination file.
    /// </remarks>
    [<ScriptableMember>]
    member this.Save() =
        let sfd = new SaveFileDialog()
        sfd.Filter <- "F# Source Files (*.fs)| *.fs|F# Script Files (*.fsx)|*.fsx"
        sfd.DefaultExt <- "fsx"
        let accepted = sfd.ShowDialog()
        if (accepted = Nullable(true)) then
            use sw = new StreamWriter(sfd.OpenFile())
            sw.Write(RichTextTool.GetTextFromRichText(txtInput))

            isDirty <- false

    /// <summary>
    /// Sends the specified code to F# Interactive for execution.
    /// </summary>
    /// <param name="code">Source code to execute.</param>
    [<ScriptableMember>]
    member this.Execute(code:string) =
        this.DoExecute(code)

    /// <summary>
    /// Resets F# Interactive.
    /// </summary>
    [<ScriptableMember>]
    member this.Reset() =
        // Interrupt current evaluation
        consoleOpt.Value.Interrupt()

        // Capture current FSI settings
        let settings = Microsoft.FSharp.Compiler.Interactive.Settings.fsi

        // Kill the event loop (we'll use another method later)
        settings.EventLoop.ScheduleRestart()

        // Prepare a new console
        fsiIn <- createFsiReader()
        let args = [| "fsi.exe"; "--define:SILVERLIGHT"; "--define:INTERACTIVE" |]
        consoleOpt <- Some (new Runner.InteractiveConsole(args, fsiIn, fsiOut, fsiOut))

        // Clear any output and launch the new session
        txtOutput.Text <- String.Empty
        this.StartFsi()

    interface IDisposable with 
        /// Releases resources used by this object.
        member this.Dispose() = 
            consoleOpt <- None
            if (fsiIn <> null) then 
                try
                    fsiIn.Dispose()
                finally
                    fsiIn <- null

            if (fsiOut <> null) then 
                try
                    fsiOut.Dispose()
                finally
                    fsiOut <- null

            if (colorizationTimer <> null) then
                try
                    colorizationTimer.Dispose()
                finally
                    colorizationTimer <- null
            GC.SuppressFinalize(this)


    //#endregion


    member x.StreamFromReader() = 
        if (fsiIn = null) then null else fsiIn.BaseStream :?> CompilerInputStream

    member x.StreamFromWriter() = 
        if (fsiOut = null) then null else fsiOut.BaseStream :?> CompilerOutputStream

    /// <summary>
    /// Handles initialization of the control.
    /// </summary>
    /// <remarks>
    /// Because most of this method deals with Silverlight UI objects,
    /// or is code that should not come before the Silverlight UI objects
    /// are set up and ready to go, we defer all of this "constructor"-type
    /// logic to this method instead, after the UI's all set up and ready
    /// to go.
    /// </remarks>
    member this.UserControl_Loaded(sender:obj, e:RoutedEventArgs) =
        btnGo.Click.Add (fun e -> this.Execute())

        btnLoad.Click.Add (fun e ->  this.LoadFromFile())

        btnSave.Click.Add (fun e -> this.Save())


        btnState1.Click.Add (fun e -> this.SetCanvasPosition(CanvasPosition.Alone))
        btnState2.Click.Add (fun e -> this.SetCanvasPosition(CanvasPosition.Right))
        btnState3.Click.Add (fun e -> 
            let pos = if btnState3.IsChecked = Nullable(true) then CanvasPosition.Right else CanvasPosition.Hidden
            this.SetCanvasPosition pos)

        txtInput.MouseRightButtonDown.Add (fun e -> e.Handled <- true)
        txtOutput.MouseRightButtonDown.Add (fun e -> e.Handled <- true)

        txtOutput.MouseRightButtonUp.Add (fun e ->  
            let cm = new ConsoleContextMenu(false, this)
            cm.ShowMenu(e.GetPosition(layoutRoot)))

        txtInput.MouseRightButtonUp.Add (fun e ->  
            let cm = new ConsoleContextMenu(true, this)
            cm.ShowMenu(e.GetPosition(layoutRoot)))


        // Set up scripting text box
        txtInput.KeyDown.Add(fun e -> 
            let modifiers = Keyboard.Modifiers
            if ((e.Key = Key.Enter) && (modifiers = ModifierKeys.Control)) then
                this.Execute()
                e.Handled <- true
            elif ((e.Key = Key.Enter) && (modifiers = (ModifierKeys.Shift ||| ModifierKeys.Control))) then
                let script = RichTextTool.GetCurrentLineAndMoveToNext(txtInput)
                this.DoExecute(script)
                e.Handled <- true
            elif ((e.Key = Key.Tab) && (modifiers = ModifierKeys.None)) then
                txtInput.Selection.Text <- txtInput.Selection.Text + TabSpace
                e.Handled <- true)

        txtInput.ContentChanged.AddHandler txtInputHandler

        RichTextTool.SetTextToRichText(txtInput, WelcomeMessage)
        isDirty <- false

        // Set up output text box
        txtOutput.Text <- String.Empty

        // Set default state for the window layout
        this.SetCanvasPosition(CanvasPosition.Hidden)

        // Set up FSI instance
        fsiIn <- createFsiReader()
        fsiOut <- createFsiWriter()
        let args = [| "fsi.exe"; "--define:SILVERLIGHT"; "--define:INTERACTIVE" |]
        consoleOpt <- Some (new Runner.InteractiveConsole(args, fsiIn, fsiOut, fsiOut))
        srcCodeServices <- Some (new Runner.SimpleSourceCodeServices(["SILVERLIGHT"; "INTERACTIVE"]))

        // Allow generated code to be interrupted
        Microsoft.FSharp.Silverlight.EmitInterruptChecks <- true

        // Start the hosted interactive compiler on a background thread.
        this.StartFsi()

        // Start component to copy FSI output to the UI elements.
        (new Thread(this.TransferOutput)).Start()

        // Setup timer for code colorization
        this.CreateColorizationTimer()

    /// <summary>
    /// Start FSI on its own thread.
    /// </summary>
    member this.StartFsi() =
        (new Thread(fun () ->
                try
                    consoleOpt.Value.Run()
                with :? ObjectDisposedException ->  ())).Start()

        this.StreamFromReader().Add("#load \"init.fsx\"\n")

    /// Transfers FSI output (std output and std error) to the output textbox.
    member this.TransferOutput() =
        let rec transfer() = 
            let strm = this.StreamFromWriter()
            if (strm <> null) then 

                let output = strm.Read()
                if (String.IsNullOrEmpty(output)) then 
                    Thread.Sleep(OutputPause)
                else
                    txtOutput.Dispatcher.BeginInvoke(fun () ->
                        txtOutput.Text <- txtOutput.Text + output
                        txtOutput.Select(txtOutput.Text.Length, 0)) |> ignore

                transfer() 
        transfer()

    member this.Execute() =
        btnGo.IsEnabled <- false

        try
            let script = RichTextTool.GetSelectedTextFromRichText(txtInput)
            this.DoExecute(script)
        finally
            btnGo.IsEnabled <- true

    member this.DoExecute(script:string) = 
        if (String.IsNullOrEmpty(script)) then () else

        //// Put the script into the input reader of FSI.
        this.StreamFromReader().Add(script)
        //// Include  token (on a new line to avoid case where last line is a comment)
        this.StreamFromReader().Add("\n;;\n")

        let limit = maxLinesToEcho
        if (limit > 0) then 
            use reader = new StringReader(script)
            let mutable count = 0
            let mutable line = reader.ReadLine()
            while (line <> null) do 
                count <- count + 1
                if (count <= limit) then 
                    if (count > 1) then 
                        fsiOut.Write("  ")

                    fsiOut.WriteLine(line)

                line <- reader.ReadLine()

            if (count > limit) then
                fsiOut.WriteLine("  [... and {0} more line(s)]", (count - limit))


    // Setup the timer which handles the background work for code colorization.
    member this.CreateColorizationTimer() =
        colorizationTimer <- new Timer(fun _ -> 
                let defs = RichTextTool.ComputeColorization(srcCodeServices.Value, sourceToColorize)
                this.Dispatcher.BeginInvoke(fun () -> this.ApplyOrColorize(defs)) |> ignore)

    /// <summary>
    /// Trigger a new colorization. If colorization is already on-going, this simply
    /// invalidates the current computation and requests that a new one be started.
    /// </summary>
    member this.Colorize(delay:int) =
        if (sourceToColorize <> null) then 
            // Colorization is on-going. Invalidate the results being computed.
            // This indicates that the colorization loop should be kept active 
            // because the content of the script window has changed.
            sourceToColorizeInvalidated <- true
        else
            let source = RichTextTool.GetTextFromRichText(txtInput)
            if not (String.IsNullOrEmpty(source)) then 
                // Kick-off the colorization loop.
                sourceToColorize <- source
                sourceToColorizeInvalidated <- false
                if (colorizationTimer <> null) then 
                    colorizationTimer.Change(delay, Timeout.Infinite) |> ignore

    /// <summary>
    /// Apply a new code colorization or kick-off a new computation of the colorized
    /// text runs if the source code has been changed.
    /// </summary>
    member this.ApplyOrColorize(blockDef: List<List<RunDefinition>> ) =
        if (sourceToColorizeInvalidated) then
            // Content has changed since we started the color computation.
            // Current run definitions are invalid. Keep the loop active.
            sourceToColorize <- null
            this.Colorize(ColorizationPause)
        else
            // Detach event handler prior to applying colorization results.
            txtInput.ContentChanged.RemoveHandler txtInputHandler

            // Preserve cursor location
            let cursorPt = RichTextTool.GetCursorPoint(txtInput)

            // Apply the new formatting
            RichTextTool.ParagraphDefinitionsToRichText(txtInput, blockDef)

            // Attempt to restore cursor position
            let currTP = txtInput.GetPositionFromPoint(cursorPt)
            if (currTP <> null) then 
                txtInput.Selection.Select(currTP, currTP)

            // Reset to null to indicate that colorization loop is now idle.
            sourceToColorize <- null

            // Re-attach event handler
            txtInput.ContentChanged.AddHandler txtInputHandler

    member this.SetCanvasPosition(position:CanvasPosition) =
        match position with 
        | CanvasPosition.Alone -> 
            btnState1.IsEnabled <- false
            btnState2.IsEnabled <- true
            btnState3.IsChecked <- Nullable true
            layoutRoot.ColumnDefinitions.[0].Width <- new GridLength(0.0, GridUnitType.Star)
            layoutRoot.ColumnDefinitions.[2].Width <- new GridLength(1.0, GridUnitType.Star)
            splitter2.Visibility <- System.Windows.Visibility.Collapsed
            btnLoad.Visibility <- System.Windows.Visibility.Collapsed
            btnSave.Visibility <- System.Windows.Visibility.Collapsed
            btnGo.Visibility <- System.Windows.Visibility.Collapsed
            canvasButtons.Visibility <- Visibility.Visible
        | CanvasPosition.Right -> 
            btnState1.IsEnabled <- true
            btnState2.IsEnabled <- false
            btnState3.IsChecked <- Nullable true
            layoutRoot.ColumnDefinitions.[0].Width <- new GridLength(0.65, GridUnitType.Star)
            layoutRoot.ColumnDefinitions.[2].Width <- new GridLength(0.35, GridUnitType.Star)
            splitter2.Visibility <- System.Windows.Visibility.Visible
            btnLoad.Visibility <- System.Windows.Visibility.Visible
            btnSave.Visibility <- System.Windows.Visibility.Visible
            btnGo.Visibility <- System.Windows.Visibility.Visible
            canvasButtons.Visibility <- Visibility.Visible
        | CanvasPosition.Hidden ->
            btnState1.IsEnabled <- true
            btnState2.IsEnabled <- true
            btnState3.IsChecked <- Nullable false
            layoutRoot.ColumnDefinitions.[0].Width <- new GridLength(1.0, GridUnitType.Star)
            layoutRoot.ColumnDefinitions.[2].Width <- new GridLength(0.0, GridUnitType.Star)
            splitter2.Visibility <- System.Windows.Visibility.Collapsed
            btnLoad.Visibility <- System.Windows.Visibility.Visible
            btnSave.Visibility <- System.Windows.Visibility.Visible
            btnGo.Visibility <- System.Windows.Visibility.Visible
            canvasButtons.Visibility <- Visibility.Collapsed

    member x.TxtInput = txtInput
    member x.TxtOutput = txtOutput

/// <summary>
/// Defines a context menu shared by the script window and output window.
/// Initializes a new instance of the ConsoleContextMenu class taking into account 
/// whether it is targetted at the script window or at the output window.
/// </summary>
and ConsoleContextMenu(isInScriptWindow: bool, consoleControl: ConsoleControl) as this = 
    inherit UserControl()   
    do Application.LoadComponent(this, new System.Uri("/FSharp.Console;component/ConsoleContextMenu.xaml", System.UriKind.Relative));
    let pasteButton : Button = this?PasteButton
    let cancelButton : Button = this?CancelButton
    let clearOutputButton : Button = this?ClearOutputButton
    let clearCanvasButton : Button = this?ClearCanvasButton
    let resetButton : Button = this?ResetButton
    let cutButton : Button = this?CutButton
    let copyButton : Button = this?CopyButton
    let menuGrid : Grid = this?MenuGrid
    let menuBorder : Border = this?MenuBorder
    let transparentCanvas : Canvas = this?TransparentCanvas

    do 

    // Configure behavior for the script window or output window
      if isInScriptWindow then 
          pasteButton.IsEnabled <- true
          cutButton.IsEnabled <- true
          cutButton.Click.Add(fun _ -> 
              Clipboard.SetText(RichTextTool.GetSelectedTextFromRichText(consoleControl.TxtInput))
              consoleControl.TxtInput.Selection.Text <- String.Empty
              this.ClosePopup())

          copyButton.Click.Add(fun _ -> 
              Clipboard.SetText(RichTextTool.GetSelectedTextFromRichText(consoleControl.TxtInput))
              this.ClosePopup())

          pasteButton.Click.Add(fun _ -> 
              consoleControl.TxtInput.Selection.Text <- Clipboard.GetText()
              this.ClosePopup())
      else
          pasteButton.IsEnabled <- false
          cutButton.IsEnabled <- false
          copyButton.Click.Add(fun _ -> 
              Clipboard.SetText(consoleControl.Output)
              this.ClosePopup())

    let popup = new Popup(Child = this)

    let Console_SizeChanged = SizeChangedEventHandler(fun sender e -> this.ClosePopup())
    let Console_LostFocus = RoutedEventHandler (fun sender e -> 
            match System.Windows.Input.FocusManager.GetFocusedElement() with
            | (:? Button as btn) when btn.Parent.Equals menuGrid -> ()
            | _ -> this.ClosePopup())

    do transparentCanvas.MouseRightButtonDown.Add (fun e -> 
            e.Handled <- true
            this.ClosePopup())

    do transparentCanvas.MouseLeftButtonDown.Add (fun e -> 
            this.ClosePopup())

    do cancelButton.Click.Add (fun _ -> 
            consoleControl.Cancel();
            this.ClosePopup())

    do resetButton.Click.Add (fun _ -> 
            consoleControl.Reset();
            this.ClosePopup())

    do  clearOutputButton.Click.Add (fun _ -> 
            consoleControl.ClearOutput();
            this.ClosePopup())
        
    do  clearCanvasButton.Click.Add (fun _ -> 
            consoleControl.ClearCanvas();
            this.ClosePopup())
 
    member this.ShowMenu(location:Point) =
        if (popup.IsOpen) then () else 
        transparentCanvas.Width <- consoleControl.ActualWidth
        transparentCanvas.Height <- consoleControl.ActualHeight
        consoleControl.SizeChanged.AddHandler Console_SizeChanged
        consoleControl.LostFocus.AddHandler Console_LostFocus
        popup.HorizontalOffset <- 0.0
        popup.VerticalOffset <- 0.0

        let x = min (max location.X 10.0) (consoleControl.ActualWidth - 175.0)
        let y = min (max location.Y 10.0) (consoleControl.ActualHeight - 200.0)
        menuBorder.Margin <- Thickness(x, y, 0.0, 0.0)

        popup.IsOpen <- true

    member this.ClosePopup() =
        consoleControl.SizeChanged.RemoveHandler Console_SizeChanged
        consoleControl.LostFocus.RemoveHandler Console_LostFocus            
        popup.IsOpen <- false
        if isInScriptWindow then
            consoleControl.TxtInput.Focus() |> ignore
        else
            consoleControl.TxtOutput.Focus() |> ignore

