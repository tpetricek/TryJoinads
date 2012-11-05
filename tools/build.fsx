#r "System.Web.dll"
#r "..\\lib\\FSharp.Markdown.dll"
#r "..\\lib\\FSharp.CodeFormat.dll"
#load "StringParsing.fs"

open System
open System.IO
open System.Web
open System.Collections.Generic

open FSharp.Patterns
open FSharp.CodeFormat
open FSharp.Markdown

let docs = Path.Combine(__SOURCE_DIRECTORY__, "..\\documents")
let out = Path.Combine(__SOURCE_DIRECTORY__, "..\\output\\docs")

let template = 
  @"<!DOCTYPE html PUBLIC ""-//W3C//DTD XHTML 1.0 Transitional//EN"" 
                        ""http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd"">
  <html xmlns=""http://www.w3.org/1999/xhtml"">
  <head>
    <title id=""title"">{0}</title>
    <link href=""http://fonts.googleapis.com/css?family=Gudea"" rel=""stylesheet"" type=""text/css"">
    <link rel=""stylesheet"" type=""text/css"" href=""{2}contentstyle.css"" />
    <script type=""text/javascript"" src=""{2}tips.js""></script>
    <script type=""text/javascript"">

      var _gaq = _gaq || [];
      _gaq.push(['_setAccount', 'UA-1561220-5']);
      _gaq.push(['_setDomainName', 'tryjoinads.org']);
      _gaq.push(['_trackPageview']);

      (function() {{
        var ga = document.createElement('script'); ga.type = 'text/javascript'; ga.async = true;
        ga.src = ('https:' == document.location.protocol ? 'https://ssl' : 'http://www') + '.google-analytics.com/ga.js';
        var s = document.getElementsByTagName('script')[0]; s.parentNode.insertBefore(ga, s);
      }})();

    </script>
  </head>
  <body class=""content"" onload=""parent.updateTitle(document.getElementById('title').innerHTML)"">
    {1}

    <!-- HTML for Tool Tips -->
    {3}
  </body>
  </html>"  

let fsharpCompiler = @"C:\tomas\Binary\FSharp.Extensions\Debug\cli\4.0\bin\FSharp.Compiler.dll"
let asm = System.Reflection.Assembly.LoadFile(fsharpCompiler)
let formatAgent = CodeFormat.CreateAgent(asm)

let (|ParseCommands|_|) (str:string) = 
  let kvs = 
    [ for cmd in str.Split(',') do
        let kv = cmd.Split('=')
        if kv.Length = 2 then yield kv.[0], kv.[1] 
        elif kv.Length = 1 then yield kv.[0], "" ] 
  if kvs <> [] then Some(dict kvs) else None

/// Extract source code from all CodeBlock elements in the document
let rec collectCodes par = seq {
  match par with 
  | CodeBlock(String.StartsWithWrapped ("[", "]") (ParseCommands cmds, String.TrimStart code)) 
  | CodeBlock(Let (dict []) (cmds, code)) ->
      if (cmds.ContainsKey("copy")) && cmds.["copy"] = "no" then ()
      else 
        let modul = 
          match cmds.TryGetValue("module") with
          | true, v -> Some v | _ -> None
        yield modul, code
  | Matching.ParagraphNested(_, nested) ->
      for par in nested |> Seq.concat do
        yield! collectCodes par
  | _ -> () }

/// Repalce CodeBlock elements with formatted HTML that 
/// was processed by the F# snippets tool
let rec replaceCodes (codeLookup:IDictionary<_, _>) = function
  | CodeBlock(String.StartsWithWrapped ("[", "]") (ParseCommands cmds, String.TrimStart code)) 
      when cmds.ContainsKey("hide") -> None
  | CodeBlock(String.StartsWithWrapped ("[", "]") (ParseCommands cmds, String.TrimStart code)) 
  | CodeBlock(Let (dict []) (cmds, code)) ->
      if (cmds.ContainsKey("copy")) && cmds.["copy"] = "no" then
        let html = "<pre>" + HttpUtility.HtmlEncode(code) + "</pre>"
        HtmlBlock(html) |> Some
      else
        let formatted : string = codeLookup.[code]
        let run = 
          if cmds.ContainsKey("noload") then ""
          elif cmds.ContainsKey("load") then
               "<br /><button class=\"load\" onclick=\'parent.loadCode(\"" + HttpUtility.JavaScriptStringEncode(code) + "\");\'></button>"
          else "<br /><button class=\"run\" onclick=\'parent.runCode(\"" + HttpUtility.JavaScriptStringEncode(code) + "\");\'></button>"
        let html = formatted.Replace("</pre>", run + "</pre>")
        let html = if cmds.ContainsKey("noload") then html 
                   else html.Replace("<pre class=\"fssnip\"", "<pre class=\"fssnip runnable\"")
        HtmlBlock(html) |> Some
  | Matching.ParagraphNested(pn, nested) ->
      Matching.ParagraphNested(pn, List.map (List.choose (replaceCodes codeLookup)) nested) |> Some
  | Matching.ParagraphSpans(ps, spans) -> Matching.ParagraphSpans(ps, spans) |> Some
  | Matching.ParagraphLeaf(pl) -> Matching.ParagraphLeaf(pl) |> Some


// Main function - process all files in the specified directory
// (and keep a relative path to the root for inserting links)
let rec build (unnest:string) subdir =
  for subdir in Directory.GetDirectories(subdir) do
    build ("../" + unnest) subdir
  for source in Directory.GetFiles(subdir, "*.text") do
    // Load the Markdown document, parse it & extract title
    printfn " - processing %s" source
    let target = source.Replace(docs, out).Replace(".text", ".html")
    let text = File.ReadAllText(source)
    let doc = Markdown.Parse(text)
    let title, paragraphs =
      match doc.Paragraphs with 
      | QuotedBlock([Paragraph([Literal title])])::pars -> 
          title, pars
      | pars -> "", pars

    // Extract all CodeBlocks and pass them to F# snippets
    let codes = paragraphs |> Seq.collect collectCodes |> Array.ofSeq
    let pars, tipHtml = 
      if codes.Length = 0 then paragraphs, ""
      else
        // Process all F# snippets using a tool
        let fsharpSource = 
          codes
          |> Seq.mapi (fun index (modul, code) ->
              match modul with
              | Some modul ->
                  // generate module & add indentation
                  "module " + modul + " =\n" +
                  "// [snippet:" + (string index) + "]\n" +
                  "    " + code.Replace("\n", "\n    ") + "\n" +
                  "// [/snippet]"
              | None ->
                  "// [snippet:" + (string index) + "]\n" +
                  code + "\n" +
                  "// [/snippet]" )
          |> String.concat "\n\n"
        // Write to Temp
        File.WriteAllText(target + ".fs", fsharpSource)
        let args = 
          "--noframework --nowarn:26" + 
          @" -r:""" + __SOURCE_DIRECTORY__ + @"\..\console\compiler\FSharp.Core.dll""" +
          @" -r:""" + __SOURCE_DIRECTORY__ + @"\..\console\bin\Debug\FSharp.Console.dll""" +
          @" -r:""" + __SOURCE_DIRECTORY__ + @"\..\src\FSharp.Joinads.Silverlight\bin\Debug\FSharp.Joinads.Silverlight.dll""" +
          //@" -r:""C:\Program Files (x86)\Microsoft Reactive Extensions SDK\v1.1.10621\Binaries\Silverlight\v5.0\System.Reactive.dll""" +
          @" -r:""C:\Tomas\Research\TryJoinads\src\FSharp.Joinads.Silverlight\bin\Debug\System.Reactive.dll""" +
          @" -r:""C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\Silverlight\v5.0\mscorlib.dll""" + 
          @" -r:""C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\Silverlight\v5.0\System.dll""" +
          @" -r:""C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\Silverlight\v5.0\System.Net.dll""" +
          @" -r:""C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\Silverlight\v5.0\System.Windows.dll"""
        let snippets, errors = formatAgent.ParseSource("C:\\temp\\TryJoinads.fsx", fsharpSource, args)
        let formatted = CodeFormat.FormatHtml(snippets, "ft", false, false)
        let snippetLookup = Array.zip (Array.map snd codes) [| for fs in formatted.SnippetsHtml -> fs.Html |] |> dict
        
        // Print errors
        for (SourceError((sl, sc), (el, ec), kind, msg)) in errors do
          printfn "   * (%d:%d)-(%d:%d) (%A): %s" sl sc el ec kind msg
        if errors.Length > 0 then printfn ""

        // Replace CodeBlocks with formatted code    
        List.choose (replaceCodes snippetLookup) paragraphs, formatted.ToolTipHtml

    // Construct new Markdown document and write it
    let newDoc = MarkdownDocument(pars, doc.DefinedLinks)
    let proc = Markdown.WriteHtml(newDoc)
    File.WriteAllText(target, String.Format(template, title, proc, unnest, tipHtml))

let make() = build "../" docs

make()
