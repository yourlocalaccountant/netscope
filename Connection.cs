using System.Net;
namespace NetScope;
record Connection(int Pid, string Protocol, string Local, IPAddress Remote, int Port, string State)
{
    public string Key => $"{Pid}|{Protocol}|{Local}|{Remote}|{Port}";
    public bool HasRemoteEndpoint => Port > 0 && FlowId.Address(Remote) is not ("0.0.0.0" or "::");
    public FlowId Flow { get { var local = IPEndPoint.Parse(Local); return FlowId.Create(Pid, Protocol, local.Address, local.Port, Remote, Port); } }
}
