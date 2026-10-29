using System.Runtime.Serialization;

namespace Fika.Core.Networking.Models;

[DataContract]
public struct CreateMatchResponse
{
    [DataMember(Name = "success")]
    public bool Success;
}
