using UnityEngine;

public class ARRotationGizmo : MonoBehaviour
{
    public float radius = 0.45f;
    public int segments = 64;
    public float lineWidth = 0.008f;

    private LineRenderer pitchCircle;
    private LineRenderer yawCircle;
    private LineRenderer rollCircle;

    void Start()
    {
        RebuildCircles();
    }

    public void Initialize(Bounds bounds)
    {
        // Set radius based on bounds extents
        float maxExtent = Mathf.Max(bounds.extents.x, bounds.extents.y, bounds.extents.z);
        radius = maxExtent * 1.15f;
        RebuildCircles();
    }

    public void RebuildCircles()
    {
        // Destroy existing circles if any
        if (pitchCircle != null) Destroy(pitchCircle.gameObject);
        if (yawCircle != null) Destroy(yawCircle.gameObject);
        if (rollCircle != null) Destroy(rollCircle.gameObject);

        // Find standard unlit shaders
        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null) shader = Shader.Find("Hidden/Internal-Colored");
        Material mat = new Material(shader);

        pitchCircle = CreateCircle("PitchCircle", Color.red, mat, Axis.X);
        yawCircle = CreateCircle("YawCircle", Color.green, mat, Axis.Y);
        rollCircle = CreateCircle("RollCircle", Color.blue, mat, Axis.Z);
    }

    private enum Axis { X, Y, Z }

    private LineRenderer CreateCircle(string name, Color color, Material mat, Axis axis)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(this.transform, false);

        LineRenderer lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = false;
        lr.startWidth = lineWidth;
        lr.endWidth = lineWidth;
        lr.positionCount = segments + 1;
        lr.material = mat;
        
        // Use color property and set start/end color
        lr.startColor = color;
        lr.endColor = color;

        Vector3[] points = new Vector3[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            float theta = (2f * Mathf.PI / segments) * i;
            float cos = Mathf.Cos(theta);
            float sin = Mathf.Sin(theta);

            if (axis == Axis.X)
            {
                // Y-Z plane
                points[i] = new Vector3(0f, radius * sin, radius * cos);
            }
            else if (axis == Axis.Y)
            {
                // X-Z plane
                points[i] = new Vector3(radius * cos, 0f, radius * sin);
            }
            else if (axis == Axis.Z)
            {
                // X-Y plane
                points[i] = new Vector3(radius * cos, radius * sin, 0f);
            }
        }

        lr.SetPositions(points);
        return lr;
    }
}
