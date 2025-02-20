using System;
using CoreRemoting.Tests.ExternalTypes;

namespace CoreRemoting.Tests.Tools
{
    public class TestService : ITestService
    {
        public Func<object, object> TestMethodFake { get; set; }
        
        public Func<object, object> BaseTestMethodFake { get; set; }

        public Action OneWayMethodFake { get; set; }
        
        public Action<DataClass> TestExternalTypeParameterFake { get; set; }
        
        public event Action ServiceEvent; 
        
        public object TestMethod(object arg)
        {
            return TestMethodFake?.Invoke(arg);
        }

        public void TestMethodWithDelegateArg(Action<string> callback)
        {
            callback("test");
        }

        public void FireServiceEvent()
        {
            ServiceEvent?.Invoke();
        }
        
        public void OneWayMethod()
        {
            OneWayMethodFake?.Invoke();
        }

        public void TestExternalTypeParameter(DataClass data)
        {
            TestExternalTypeParameterFake?.Invoke(data);
        }

        public string Echo(string text)
        {
            return text;
        }

        public object BaseTestMethod(object arg)
        {
            return BaseTestMethodFake?.Invoke(arg);
        }
    }
}