//----------------------------------------------------------------------------
//
// Copyright (c) 2002-2011 Microsoft Corporation. 
//
// This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
// copy of the license can be found in the License.html file at the root of this distribution. 
// By using this source code in any fashion, you are agreeing to be bound 
// by the terms of the Apache License, Version 2.0.
//
// You must not remove this notice, or any other, from this software.
//----------------------------------------------------------------------------

namespace Samples.ConsoleApp

open System
open System.Windows
open System.Windows.Browser
open System.Windows.Threading

type App() as this = 
    inherit Application()
    do Application.LoadComponent(this, new System.Uri("/Samples.ConsoleApp;component/App.xaml", System.UriKind.Relative));
    let console = new ConsoleControl()
    do this.Startup.Add (fun _ -> 
            this.RootVisual <- console
            // Make the control available to JavaScript code
            HtmlPage.RegisterScriptableObject("FsiConsole", console)
        )
    do this.Exit.Add (fun _ -> 
        (console :> IDisposable).Dispose())

    do this.UnhandledException.Add (fun e -> 
        // If the app is running outside of the debugger then report the exception open
        // the browser's exception mechanism. On IE this will display it a yellow alert 
        // icon in the status bar and Firefox will display a script error.
        if not System.Diagnostics.Debugger.IsAttached then 
            // NOTE: This will allow the application to continue running after an exception has been thrown
            // but not handled. 
            // For production applications this error handling should be replaced with something that will 
            // report the error to the website and stop the application.
            e.Handled <- true
            Deployment.Current.Dispatcher.BeginInvoke(Action(fun () -> 
                try
                    let errorMsg = e.ExceptionObject.Message + e.ExceptionObject.StackTrace
                    let errorMsg = errorMsg.Replace('"', '\'').Replace("\r\n", @"\n")
                    System.Windows.Browser.HtmlPage.Window.Eval("throw new Error(\"Unhandled Error in Silverlight Application " + errorMsg + "\")") |> ignore
                with _ -> ()))
              |> ignore)



