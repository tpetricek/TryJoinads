$().ready(function ()
{
  var space = 150;
  $("#wrapper").height(window.innerHeight - space);
  $("#wrapper").splitter();

  $("#content").height(window.innerHeight - space - 32 - 40);
  $("#slplaceholder").height(window.innerHeight - space - 32 - 40);

  $.ajax({
    url: "docs/intro.html",
    success: function (data)
    {
      $("#content").html(data);
      $("#content_title").html($("#title").html());
      $("#title").remove();
    }
  });
});

function loadCode(code)
{
  var cons = document.getElementById("fsiConsole");
  cons.Content.FsiConsole.Script += "\n" + code;
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