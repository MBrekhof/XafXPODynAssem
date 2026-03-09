using System.Linq;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Actions;
using DevExpress.ExpressApp.Security;
using DevExpress.Persistent.Base;
using XafXPODynAssem.Module.BusinessObjects;
using XafXPODynAssem.Module.Services;

namespace XafXPODynAssem.Module.Controllers
{
    public class SchemaChangeController : ViewController<ListView>
    {
        private SimpleAction _deployAction;

        public SchemaChangeController()
        {
            TargetObjectType = typeof(CustomClass);

            _deployAction = new SimpleAction(this, "DeploySchema", PredefinedCategory.Edit)
            {
                Caption = "Deploy Schema",
                ConfirmationMessage = "Deploy all runtime schema changes? The server may briefly restart.",
                ImageName = "Action_Reload",
                ToolTip = "Compile and deploy all runtime entity changes",
            };
            _deployAction.Execute += DeployAction_Execute;
        }

        private void DeployAction_Execute(object sender, SimpleActionExecuteEventArgs e)
        {
            // Record deploy in schema history
            try
            {
                var history = ObjectSpace.CreateObject<SchemaHistory>();
                history.Action = SchemaChangeAction.Deploy;
                history.UserName = SecuritySystem.CurrentUserName;
                history.Summary = "Schema deployed via Deploy Schema action";

                // Capture current runtime class names
                var classes = ObjectSpace.GetObjects<CustomClass>()
                    .Cast<CustomClass>()
                    .Where(c => c.Status == CustomClassStatus.Runtime)
                    .Select(c => c.ClassName)
                    .ToList();
                history.Details = $"Runtime classes: {string.Join(", ", classes)}";

                ObjectSpace.CommitChanges();
            }
            catch (Exception ex)
            {
                Tracing.Tracer.LogError($"Failed to record deploy history: {ex.Message}");
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await SchemaChangeOrchestrator.Instance.ExecuteHotLoadAsync();
                }
                catch (Exception ex)
                {
                    Tracing.Tracer.LogError($"Deploy schema failed: {ex.Message}");
                }
            });
        }
    }
}
