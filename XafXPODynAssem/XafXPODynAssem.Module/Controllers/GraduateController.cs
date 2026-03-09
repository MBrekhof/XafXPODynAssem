using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Actions;
using DevExpress.Persistent.Base;
using XafXPODynAssem.Module.BusinessObjects;
using XafXPODynAssem.Module.Services;

namespace XafXPODynAssem.Module.Controllers
{
    /// <summary>
    /// Adds a "Graduate" action to CustomClass DetailView.
    /// Generates production XPO C# source and changes status to Compiled.
    /// After graduation, the entity is no longer included in runtime compilation.
    /// </summary>
    public class GraduateController : ViewController<DetailView>
    {
        private SimpleAction _graduateAction;

        public GraduateController()
        {
            TargetObjectType = typeof(CustomClass);

            _graduateAction = new SimpleAction(this, "GraduateEntity", "SchemaManagement")
            {
                Caption = "Graduate",
                ConfirmationMessage = "WARNING: This will mark the entity as Compiled and exclude it from runtime compilation on next Deploy.\n\n" +
                    "After graduation:\n" +
                    "- The generated C# source will be stored in the 'Graduated Source' field\n" +
                    "- The entity's nav item will disappear after the next Deploy\n" +
                    "- The database table and data are preserved\n" +
                    "- To undo, change Status back to Runtime\n\n" +
                    "Continue?",
                ImageName = "Action_Grant",
                ToolTip = "Generate production C# source and mark as Compiled (excluded from runtime)",
            };
            _graduateAction.Execute += GraduateAction_Execute;
        }

        protected override void OnActivated()
        {
            base.OnActivated();
            UpdateActionState();
            View.CurrentObjectChanged += (_, _) => UpdateActionState();
        }

        private void UpdateActionState()
        {
            var cc = View.CurrentObject as CustomClass;
            _graduateAction.Enabled.SetItemValue("StatusCheck",
                cc != null && cc.Status == CustomClassStatus.Runtime);
        }

        private void GraduateAction_Execute(object sender, SimpleActionExecuteEventArgs e)
        {
            var cc = (CustomClass)View.CurrentObject;

            GraduationService.Graduate(cc);
            ObjectSpace.CommitChanges();

            Application.ShowViewStrategy.ShowMessage(
                $"Entity '{cc.ClassName}' graduated successfully. " +
                "The generated C# source is stored in the 'Graduated Source' field. " +
                "Deploy Schema to remove it from runtime.",
                InformationType.Success);
        }
    }
}
