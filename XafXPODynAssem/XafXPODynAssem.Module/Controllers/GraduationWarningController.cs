using DevExpress.ExpressApp;
using XafXPODynAssem.Module.BusinessObjects;

namespace XafXPODynAssem.Module.Controllers
{
    /// <summary>
    /// Shows visual warnings for graduated (non-Runtime) entities.
    /// DetailView: warning message when viewing a graduated entity.
    /// ListView: info message when graduated entities exist.
    /// </summary>
    public class GraduationWarningDetailController : ObjectViewController<DetailView, CustomClass>
    {
        protected override void OnActivated()
        {
            base.OnActivated();
            View.CurrentObjectChanged += View_CurrentObjectChanged;
            ShowWarningIfGraduated();
        }

        protected override void OnDeactivated()
        {
            View.CurrentObjectChanged -= View_CurrentObjectChanged;
            base.OnDeactivated();
        }

        private void View_CurrentObjectChanged(object sender, EventArgs e) => ShowWarningIfGraduated();

        private void ShowWarningIfGraduated()
        {
            var cc = View.CurrentObject as CustomClass;
            if (cc == null) return;

            if (cc.Status == CustomClassStatus.Compiled)
            {
                Application.ShowViewStrategy.ShowMessage(
                    $"'{cc.ClassName}' has been graduated to compiled code. " +
                    "It is excluded from runtime compilation and can no longer be modified through the UI. " +
                    "To re-activate it, change Status back to Runtime.",
                    InformationType.Warning);
            }
            else if (cc.Status == CustomClassStatus.Graduating)
            {
                Application.ShowViewStrategy.ShowMessage(
                    $"'{cc.ClassName}' is in a transitional state (Graduating). " +
                    "Set Status to Runtime or Compiled to resolve.",
                    InformationType.Warning);
            }
        }
    }

    /// <summary>
    /// Shows a warning on the CustomClass ListView when graduated entities exist,
    /// since they won't be included in Deploy Schema.
    /// </summary>
    public class GraduationWarningListController : ObjectViewController<ListView, CustomClass>
    {
        protected override void OnActivated()
        {
            base.OnActivated();
            View.CollectionSource.CollectionChanged += CollectionSource_CollectionChanged;
        }

        protected override void OnDeactivated()
        {
            View.CollectionSource.CollectionChanged -= CollectionSource_CollectionChanged;
            base.OnDeactivated();
        }

        private void CollectionSource_CollectionChanged(object sender, EventArgs e) => CheckForGraduatedEntities();

        protected override void OnViewControlsCreated()
        {
            base.OnViewControlsCreated();
            CheckForGraduatedEntities();
        }

        private void CheckForGraduatedEntities()
        {
            var objects = ObjectSpace.GetObjects<CustomClass>();
            var graduatedCount = objects.Cast<CustomClass>().Count(cc => cc.Status != CustomClassStatus.Runtime);

            if (graduatedCount > 0)
            {
                Application.ShowViewStrategy.ShowMessage(
                    $"{graduatedCount} entity(ies) have non-Runtime status and will be excluded from Deploy Schema.",
                    InformationType.Warning);
            }
        }
    }
}
