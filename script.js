function updateTitle(title)
{
  $("#content_title").html(title);
}

$().ready(function ()
{
  var space = 150;
  $("#wrapper").height(window.innerHeight - space);
  $("#wrapper").splitter({
    onstart: function () { $("#content_hider").show(); },
    onstop: function () { $("#content_hider").hide(); }
  });

  $("#content").height(window.innerHeight - space - 32 - 25);
  $("#slplaceholder").height(window.innerHeight - space - 32 - 40);

  var str = document.location + "";
  var idx = str.indexOf('?');
  if (idx > -1)
  {
    var src = str.substr(idx + 1);
    document.getElementById("content").src = "/docs/" + src;
  }
});

function runCode(code)
{
  var cons = document.getElementById("fsiConsole");
  cons.Content.FsiConsole.Script += "\n" + code + "\n";
  cons.Content.FsiConsole.Execute(code);
}

function loadCode(code)
{
  var cons = document.getElementById("fsiConsole");
  cons.Content.FsiConsole.Script += "\n" + code + "\n";
}

function loadSilverlight()
{
  $.ajax({
    url: "fsi.html",
    success: function (data)
    {
      $("#right").html(data);
    }
  });
}