TryJoinads.org source code
==========================

This repository contains the source code of the [TryJoinads.org](http://tryjoinads.org) project.
The source code contains the (slightly modified) Silverlight F# Interactive console (see `console`),
Markdown source code for the tutorials (`documents`) and generated HTML files (`output`). 
The `src` directory contains implementations of joinads for various F# computation types
(including `Async<'T>`, `Task<'T>` and some others). 

The HTML pages are automatically generated using a script `tools/build.fsx`, which uses
F# Markdown parser and F# Source Code formatter available in the `lib` directory 
(the source of both tools is [available on GitHub too](https://github.com/tpetricek/FSharp.Formatting)).

License
-------

 * **Documents** - the documents available in the repository (mainly `text` and `html` files)
   are available under the Creative Commons Attribution 2.5 license. 
   This means that you can copy, distribute and remix the work, but
   you must attribute the work to the author (by providing a link
   to the original source and my name).

 * **Source code** - the source code is available under 
   the Apache 2 license. This means that you can use it in any way,
   including commercial applications. For more information see
   [OSI web page](http://www.opensource.org/licenses/Apache-2.0).

Contributions welcome!
----------------------

If you write an interesting example that uses the `match!` notation that you would like
to share, feel free to submit a pull request. This would typically include:

  * A tutorial written in Markdown `text` file (in the `documents` directory)
  * Some addition to the `FSharp.Joinads.Silverlight` project (the `src` directory)
    if you want to write your own computation expression.

I'm also happy to use the TryJoinads web site to share other experimental F# extensions.
If you made some changes to the F# compiler that you would like to share, you'll need
to submit your pull request to [the FSharp.Extensions project](https://github.com/tpetricek/FSharp.Extensions)
which contains F# compiler source code.

Credits
-------

If you want to get in touch, you can contact me at [tomas@tomasp.net](mailto:tomas@tomasp.net) 
or using Twitter at [@tomaspetricek](http://twitter.com/tomaspetricek).