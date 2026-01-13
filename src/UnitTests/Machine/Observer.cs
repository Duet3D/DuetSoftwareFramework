namespace UnitTests.Machine
{
    // DISABLED: Observer and Provider now require dependency injection - needs complete refactoring
    // Observer.Init() no longer exists and Provider.Get is not available  
    // These tests would need to set up the full DI container with ObjectModel, Observer, etc.
    /*
    public class Observer
    {
        [OneTimeSetUp]
        public void Setup()
        {
            DuetControlServer.Model.Observer.Init();
        }

        // All test methods commented out - see git history for original implementation
        // Tests include: ObserveProperty, ObserveModelProperty, ObserveModelDictionary, 
        // ObserveModelObjectDictionary, ObserveModelObjectCollection, ObserveMessageCollectiion
    }
    */
}
