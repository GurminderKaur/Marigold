namespace Marigold;

/// <summary>Someone you remember. Everything here stays in your Marigold folder on this computer.</summary>
public sealed class Person
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Years { get; set; } = "";          // free text, e.g. "1931 – 2019"
    public string Relation { get; set; } = "";       // e.g. "my grandmother"
    public string Memories { get; set; } = "";       // written by you: sayings, habits, stories
    public List<string> Offerings { get; set; } = new();  // favourite foods, songs, things for the ofrenda
    public string Language { get; set; } = "English";
    public List<Photo> Photos { get; set; } = new();
    public Remembrance? Remembrance { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public enum PhotoStatus { Waiting, Described, Error }

public sealed class Photo
{
    public string Id { get; set; } = "";
    public string Ext { get; set; } = ".jpg";
    public string Caption { get; set; } = "";        // short; suggested by the model, yours to change
    public string Description { get; set; } = "";    // what the model saw
    public string Era { get; set; } = "";            // model's guess, e.g. "1950s", or ""
    public bool CaptionEdited { get; set; }
    public PhotoStatus Status { get; set; } = PhotoStatus.Waiting;
    public string? Error { get; set; }
    public double? Seconds { get; set; }
}

public sealed class Remembrance
{
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>Names, places, numbers or dates in the text that aren't in anything you wrote: possible inventions.</summary>
    public List<string> Ghosts { get; set; } = new();
    public string Model { get; set; } = "";
    public double Seconds { get; set; }
    public bool Edited { get; set; }
    public DateTime WhenUtc { get; set; } = DateTime.UtcNow;
}

public sealed record PersonEdit(string? Name, string? Years, string? Relation, string? Memories, List<string>? Offerings, string? Language);
public sealed record CaptionEdit(string Caption);
public sealed record TextEdit(string Title, string Text);
