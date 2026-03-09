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
            // Capture current runtime class names for audit
            string deployDetails;
            try
            {
                var classes = ObjectSpace.GetObjects<CustomClass>()
                    .Cast<CustomClass>()
                    .Where(c => c.Status == CustomClassStatus.Runtime)
                    .Select(c => c.ClassName)
                    .ToList();
                deployDetails = $"Runtime classes: {string.Join(", ", classes)}";
            }
            catch
            {
                deployDetails = "(could not capture class list)";
            }

            var userName = SecuritySystem.CurrentUserName;

            _ = Task.Run(async () =>
            {
                bool success = false;
                string error = null;
                try
                {
                    await SchemaChangeOrchestrator.Instance.ExecuteHotLoadAsync();
                    success = true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    Tracing.Tracer.LogError($"Deploy schema failed: {ex.Message}");
                }

                // Record deploy result in schema history
                try
                {
                    var connStr = XafXPODynAssemModule.RuntimeConnectionString;
                    if (!string.IsNullOrEmpty(connStr))
                    {
                        using var conn = new Microsoft.Data.SqlClient.SqlConnection(connStr);
                        conn.Open();
                        using var cmd = new Microsoft.Data.SqlClient.SqlCommand(
                            @"INSERT INTO [SchemaHistory] ([Oid], [Timestamp], [UserName], [Action], [Summary], [Details])
                              VALUES (@oid, @ts, @user, @action, @summary, @details)", conn);
                        cmd.Parameters.AddWithValue("@oid", Guid.NewGuid());
                        cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow);
                        cmd.Parameters.AddWithValue("@user", userName ?? "");
                        cmd.Parameters.AddWithValue("@action", (int)SchemaChangeAction.Deploy);
                        cmd.Parameters.AddWithValue("@summary", success ? "Schema deployed successfully" : $"Schema deploy failed: {error}");
                        cmd.Parameters.AddWithValue("@details", deployDetails);
                        cmd.ExecuteNonQuery();
                    }
                }
                catch (Exception ex)
                {
                    Tracing.Tracer.LogError($"Failed to record deploy history: {ex.Message}");
                }
            });
        }
    }
}
