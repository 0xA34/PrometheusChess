namespace PrometheusVulkan.UI.Screens;

public interface IScreen
{
    void OnShow();
    void OnHide();
    void Update(float deltaTime);
    void Render();
}
