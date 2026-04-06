using ConsoleAppFramework;
using DryDB.Cli;

var app = ConsoleApp.Create();
app.Add<Commands>();
app.Run(args);