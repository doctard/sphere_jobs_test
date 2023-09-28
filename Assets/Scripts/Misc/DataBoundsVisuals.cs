using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class DataBoundsVisuals : MonoBehaviour
{
    private JobController controller;

    void Start()
    {
        var job_controller = GetComponentInParent<JobController>();
        if (job_controller != null)
            job_controller.onBoundsChanged += OnBoundsChange;
        controller = job_controller;
    }

    void OnBoundsChange()
    {
        transform.localScale = controller.data.bounds_properties.bounds.size;
        transform.localPosition = controller.data.bounds_properties.bounds.center;
    }
}
