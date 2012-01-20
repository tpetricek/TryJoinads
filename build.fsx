#r "System.Web.dll"
#r "lib\\FSharp.Markdown.dll"
open System.IO
open System.Web
open FSharp.Markdown

let docs = Path.Combine(__SOURCE_DIRECTORY__, "documents")
let out = Path.Combine(__SOURCE_DIRECTORY__, "output\\docs")

let rec replaceCodes = function
  | CodeBlock(code) -> 
      let run = "<br /><button onclick=\"loadCode('" + HttpUtility.JavaScriptStringEncode(code) + "');\"></button>"
      let html = "<pre class=\"runnable\">" + HttpUtility.HtmlEncode(code) + run + "</pre>"
      HtmlBlock(html)
  | Matching.ParagraphNested(pn, nested) ->
      Matching.ParagraphNested(pn, List.map (List.map replaceCodes) nested)
  | Matching.ParagraphSpans(ps, spans) -> Matching.ParagraphSpans(ps, spans)
  | Matching.ParagraphLeaf(pl) -> Matching.ParagraphLeaf(pl)

let build () =
  for source in Directory.GetFiles(docs, "*.text") do
    let text = File.ReadAllText(source)
    let doc = Markdown.Parse(text)
    let pars = List.map replaceCodes doc.Paragraphs
    let newDoc = MarkdownDocument(pars, doc.DefinedLinks)
    let proc = Markdown.WriteHtml(newDoc)
    let target = Path.Combine(out, Path.GetFileNameWithoutExtension(source).ToLower() + ".html")
    File.WriteAllText(target, proc)

build ()