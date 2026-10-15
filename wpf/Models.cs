using System;

namespace LynkSignTokenHelper
{
    public sealed class AccountInfo
    {
        public string id { get; set; }
        public string display_name { get; set; }
    }
    public sealed class CandidateInfo
    {
        public string id { get; set; }
        public string device_id { get; set; }
        public string source { get; set; }
        public string Label { get; set; }
    }
    public sealed class AppState
    {
        public string message { get; set; }
        public bool logged_in { get; set; }
        public string username { get; set; }
        public string server_address { get; set; }
        public bool trusted { get; set; }
        public bool capturing { get; set; }
        public int connections { get; set; }
        public int lynk_requests { get; set; }
        public CandidateInfo[] candidates { get; set; }
        public bool confirmation_required { get; set; }
        public AccountInfo account { get; set; }
        public string previous_account_id { get; set; }
        public bool busy { get; set; }
        public string submission { get; set; }
        public int selected_candidate { get; set; }
        public string captured_at { get; set; }
    }
    public sealed class CoreSnapshot
    {
        public string type { get; set; }
        public int protocol { get; set; }
        public AppState state { get; set; } = new AppState();
        public string token { get; set; }
        public string device_id { get; set; }
        public string default_server { get; set; }
        public int selected { get; set; }
        public long request_id { get; set; }
        public bool pending { get; set; }
        public bool closing { get; set; }
        public string error { get; set; }
    }
    public interface ICoreClient : IDisposable
    {
        event Action<CoreSnapshot> Updated;
        event Action<string> Faulted;
        event Action Closed;
        void Start();
        long Command(string action, object input = null);
        void Select(int index);
        void Close();
    }
}
