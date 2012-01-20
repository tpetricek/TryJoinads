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

open System.Collections.Generic
open System.IO
open System.Text
open System.Windows
open System.Windows.Controls
open System.Windows.Documents
open System.Windows.Media
open Microsoft.FSharp.Compiler.Interactive
open Microsoft.FSharp.Compiler.SourceCodeServices

type RunDefinition = { mutable Text : string; mutable Foreground: SolidColorBrush }

type RichTextTool() =
    // Create color palette used for syntax highlighting
    static let BlackBrush = SolidColorBrush(Colors.Black)
    static let BlueBrush = SolidColorBrush(Colors.Blue)
    static let DarkRedBrush = SolidColorBrush(Color.FromArgb(255uy, 128uy, 0uy, 0uy))
    static let GrayBrush = SolidColorBrush(Colors.Gray)
    static let GreenBrush = SolidColorBrush(Colors.Green)

    /// Returns the entire text in the specified RichTextBox.
    static member GetTextFromRichText(rtb: RichTextBox) = 
        let start = rtb.Selection.Start
        let fin = rtb.Selection.End

        rtb.SelectAll()
        let text = rtb.Selection.Text

        rtb.Selection.Select(start, fin)

        text

    /// Returns the currently selected text in the specified RichTextBox.
    /// If no text is selected, the entire content is returned.
    static member GetSelectedTextFromRichText(rtb: RichTextBox) = 
        if (rtb.Selection.Text.Length < 1) then
            RichTextTool.GetTextFromRichText(rtb)
        else 
            rtb.Selection.Text

    /// Sets the specified text as the content of the given RichTextBox control.
    /// The current text is overwritten.
    static member SetTextToRichText( rtb: RichTextBox, text: string ) = 
        rtb.SelectAll()
        rtb.Selection.Text <- text

    /// Appends the specified text to the content of the given RichTextBox control.
    static member AppendTextToRichText(rtb: RichTextBox, text: string) = 
        rtb.SelectAll()
        let mutable current = rtb.Selection.Text
        if (current = null) then 
            current <- System.String.Empty

        rtb.Selection.Text <- current + text

    /// Returns a Point describing the current cursor insertion point in 
    /// the specified RichTextBox.
    static member GetCursorPoint(rtb: RichTextBox) = 
        let selEnd = rtb.Selection.End
        let fwdRect = selEnd.GetCharacterRect(LogicalDirection.Forward)
        let bkdRect = selEnd.GetCharacterRect(LogicalDirection.Backward)
        fwdRect.Union(bkdRect)
        let ret = Point(fwdRect.X + fwdRect.Width / 2.0, fwdRect.Y + fwdRect.Height / 2.0)
        ret

    /// Encapsulates logic to support Ctrl-Shift-Enter.
    static member GetCurrentLineAndMoveToNext(rtb: RichTextBox) =
        // If text is selected, determine whether the selection is on a single-line
        // or spans multiple-line. Abort if it is the latter.
        match rtb.Selection with 
        | null -> null 
        | selection -> 

        let selectedText = selection.Text
        let lineOpt = 
            if System.String.IsNullOrEmpty(selectedText) then
                let mutable line = System.String.Empty
                use reader = new StringReader(selectedText) 
                let line = reader.ReadLine()
                if (reader.ReadLine() <> null) then
                    // Selection spans multiple lines. Give up.
                    None
                else
                    Some line
            else
                None

        match lineOpt, selection.Start.Parent with
        | Some line, (:? Run as r) -> 
            match r.ElementStart.Parent with 
            | :? Paragraph as p -> 

                // We have a single-line selection anchored in paragraph p.
                // Walk the Run objects of the given paragraph to build the 
                // text of the selected line.
                let sb = new StringBuilder()
                for inl in p.Inlines do 
                    match inl with 
                    | :? Run as run -> sb.Append(run.Text) |> ignore
                    | _ -> ()

                // Figure out where to put the cursor: at beginning of next line
                // if there is one, otherwise at beginning of current line.
                let mutable first = p.Inlines.[0] :?> Run
                let mutable last = p.Inlines.[p.Inlines.Count - 1] :?> Run
                if (last <> null) then 
                    let nxt = last.ElementEnd.GetNextInsertionPosition(LogicalDirection.Forward)
                    if (nxt <> null) then 
                        let nxtRun = nxt.Parent :?> Run
                        if (nxtRun <> null) then 
                            first <- nxtRun

                    if (first <> null) then 
                        // We found a cursor location. Set it.
                        rtb.Selection.Select(first.ElementStart, first.ElementStart)

                sb.ToString()
            | _ -> null
        | _ -> null

    /// <summary>
    /// Breaks the specified source code into a list of (string, color) pairs. 
    /// </summary>
    /// <remarks>
    /// <para>
    /// A RunDefinition is a (string, color) pair. It will become a Run in the RichTextBox model.
    /// A list of RunDefinition instances will become a Paragraph. The overall content of the
    /// RichTextBox is a list of paragraphs, hence the return type of this method.
    /// </para>
    /// <para>
    /// This method does not have to execute on the UI thread.
    /// </para>
    /// </remarks>
    static member  ComputeColorization(srcCodeServices: Runner.SimpleSourceCodeServices, source: string) : List<List<RunDefinition>> = 
        let blocks = new List<List<RunDefinition>>()
        use reader = new StringReader(source) 
        let mutable line = reader.ReadLine()

        // Tokenizer state
        let mutable state = 0L
        while (line <> null) do
            let (tokens, st) = srcCodeServices.TokenizeLine(line, state)
            state <- st

            let p = new List<RunDefinition>()

            let mutable rdef = { Text = line; Foreground = BlackBrush }
            p.Add(rdef)
            //// Variable 'left' keeps track of where 'rdef.Text' begins in the current 'line'
            let mutable left = 0
            for token in tokens do
                let brush = 
                   match token.ColorClass with 
                    | TokenColorKind.Comment -> GreenBrush
                    | TokenColorKind.InactiveCode -> GrayBrush
                    | TokenColorKind.Keyword 
                    | TokenColorKind.PreprocessorKeyword -> BlueBrush
                    | TokenColorKind.String -> DarkRedBrush
                    | _ -> BlackBrush

                // Only start a new run if color changes
                if (brush <> rdef.Foreground) then 
                    // The first run in the line is an exception. We can just change its color.
                    if (token.LeftColumn = 0) then 
                        rdef.Foreground <- brush
                    else
                        rdef.Text <- line.Substring(left, token.LeftColumn - left)
                        left <- left + rdef.Text.Length
                        rdef <- { Text = line.Substring(token.LeftColumn); Foreground = brush }
                        p.Add(rdef)

            blocks.Add(p)
            line <- reader.ReadLine()

        // If source ends with a line break, add a paragraph with an empty run
        if (source.EndsWith("\n") || source.EndsWith("\r")) then
            let p = new List<RunDefinition>()
            let rdef = { Text = System.String.Empty; Foreground = BlackBrush }
            p.Add(rdef)
            blocks.Add(p)

        blocks

    /// <summary>
    /// Update the content of the specified RichTextBox to match the content defined by
    /// the given list of paragraph definitions.
    /// </summary>
    /// <remarks>
    /// Caller must invoke this method on the UI thread.
    /// </remarks>
    static member ParagraphDefinitionsToRichText(rtb:RichTextBox, paragraphDefs: List<List<RunDefinition>>) =
        // Count number of paragraphs that we can keep unmodified (rtb.Blocks[0..numToKeep]).
        let numToKeep = 
            (rtb.Blocks,paragraphDefs) 
            ||> Seq.zip 
            |> Seq.takeWhile (fun (b,defs) -> 
                match b with 
                | :? Paragraph as p -> 
                        (p.Inlines.Count = defs.Count) &&
                        (p.Inlines, defs) ||> Seq.forall2 (fun r d -> 
                            match r with 
                            | :? Run as r -> (r <> null) && (r.Foreground.Equals d.Foreground) && (r.Text = d.Text)
                            | _ -> false)
                | _ -> false)
            |> Seq.length


        // Remove blocks that are obsolete
        let numToRemove = rtb.Blocks.Count - numToKeep
        for i in 0 .. numToRemove - 1  do
            rtb.Blocks.RemoveAt(numToKeep)    

        // Add new blocks
        let numToAdd = paragraphDefs.Count - numToKeep
        for i in 0 .. numToAdd - 1  do
            let paragraphDef = paragraphDefs.[numToKeep + i]
            let paragraph = Paragraph()
            for runDef in paragraphDef do
                let r = Run(Text = runDef.Text, Foreground = runDef.Foreground)
                paragraph.Inlines.Add(r)

            rtb.Blocks.Add(paragraph)

