namespace Glance;

class Conversation(byte[] jpeg, string model, string mode)
{
    public byte[] Jpeg { get; } = jpeg;
    public string Model { get; } = model;
    public string Mode { get; } = mode;
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
}
