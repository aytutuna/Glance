namespace Glance;

class Conversation(byte[] jpeg, string model, string mode, IProvider provider)
{
    public byte[] Jpeg { get; } = jpeg;
    public string Model { get; set; } = model;
    public string Mode { get; } = mode;
    public IProvider Provider { get; } = provider;
    public string Pending { get; private set; } = Prompts.First(mode);
    public List<(string Question, string Answer)> Done { get; } = new();

    public void Ask(string question)
    {
        Pending = question;
    }

    public void Complete(string answer)
    {
        Done.Add((Pending, answer));
    }

    public IEnumerable<(bool User, string Text)> Turns()
    {
        foreach (var (question, answer) in Done)
        {
            yield return (true, question);
            yield return (false, answer);
        }
        yield return (true, Pending);
    }
}
