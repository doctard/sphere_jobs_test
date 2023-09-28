/// sources:
/// https://allenchou.net/2020/01/dot-product-projection-reflection/ - sphere bounce on plane
/// https://www.habrador.com/tutorials/math/4-plane-ray-intersection/ - ray intersection with plane
/// https://github.com/Unity-Technologies/Megacity-Sample/tree/master/Assets/Scripts/Utils/KDTree - kdtree for sphere collisions with eachother


using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Jobs;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine.Jobs;
using Unity.Burst;

[ExecuteInEditMode]
public class JobController : MonoBehaviour
{

    #region Parameters
    public int spheres_to_spawn_on_click_min = 1;
    public int spheres_to_spawn_on_click_max = 10;
    public int desired_obj_count;
    public Bounds bounds;
    public GameObject obj_prefab;
    public float sphere_radius = 0.5f;
    public bool collisions_between_spheres = true;
    #endregion

    #region Variables/non parameters
    private bool collision_between_spheres_last_frame = false;
    public delegate void OnSettingsChange();
    public OnSettingsChange onBoundsChanged;
    private Transform sphere_container;
    // sphere visuals pooling
    public class SphereVisuals
    {
        public Renderer[] renderers;
        public Transform transform;
        public bool active;
        public bool Enable(bool active)
        {
            if (this.active == active)
                return false;
            this.active = active;
            transform.gameObject.SetActive(active);
            return true;
        }
    }
    private List<SphereVisuals> sphere_visuals;
    // property block to change sphere colors
    private MaterialPropertyBlock mat_block;
    // job data/pointers
    public Data data;
    #endregion

    #region Jobs

    public unsafe struct Data
    {
        // how many active objects
        public int count;
        // position before current jobs run. Not entirely necessary, but if we potentially want more precise collision tracking we might need it (like if velocity is too high for 1 frame, but we still want to know we collided)
        // for the task, i don't know if it's necessary, so this array is here just in case
        public NativeArray<float3> old_positions;
        // position after velocity update/collision with bounds
        public NativeArray<float3> new_positions;
        // velocities of objects
        public NativeArray<float3> velocities;
        // colors of spheres when they get spawned
        public NativeArray<float3> original_colors;
        // color of spheres after jobs - either their original color, or the color for collisions
        public NativeArray<float3> final_colors;
        // used to only update material property blocks when needed
        public NativeArray<bool> colors_dirty;

        public const int max_collisions_between_spheres = 6;
        // if the current object is already in a collision - to not have to do multiple collision checks
        public NativeArray<bool> in_collision;
        // used to modify transform properties within the jobs
        public TransformAccessArray taa;
        // used for initial spawn positions, and checking if bounds have changed;
        public BoundsProperties bounds_properties;
        // the positions/normals of each wall of the bounds, used for calculating if a sphere has collided with it
        public NativeArray<BoundsPlane> bounds_planes;
        public const int bound_planes_count = 6;

        // sphere radius, could be anything above 0, used for collision check between bounds/other spheres
        public float sphere_radius;
        // if at 1, no velocity loss when bouncing with wall. Not necessary, but might be useful if we add more physics later
        public float sphere_bounciness;
        // info needed for object collision
        public KDTree kdtree;
        // random
        public Unity.Mathematics.Random rand;

        // pointers to all of the above arrays
        public Pointers* pointers;

        public unsafe void Init(Bounds bounds, Quaternion rot, Vector3 position, Vector3 scale, float sphere_radius, float sphere_bounciness)
        {
            rand = new Unity.Mathematics.Random(1);
            this.sphere_radius = sphere_radius;
            this.sphere_bounciness = sphere_bounciness;
            pointers = (Pointers*)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<Pointers>(), UnsafeUtility.AlignOf<Pointers>(), Allocator.Persistent);
            pointers->This = pointers;
            SetSize(0);
            CalcBoundPlanes(bounds, rot, position, scale);
        }

        // called whenever we changed the size of the arrays, or the application quits
        public void DisposeObjectArrays()
        {
            if (old_positions.IsCreated)
                old_positions.Dispose();
            if (new_positions.IsCreated)
                new_positions.Dispose();
            if (velocities.IsCreated)
                velocities.Dispose();
            if (original_colors.IsCreated)
                original_colors.Dispose();
            if (final_colors.IsCreated)
                final_colors.Dispose();
            if (colors_dirty.IsCreated)
                colors_dirty.Dispose();
            if (in_collision.IsCreated)
                in_collision.Dispose();
            if (kdtree.IsCreated)
                kdtree.Dispose();
            if (taa.isCreated)
                taa.Dispose();
        }

        // called when bounds change or the application quits
        private void DisposeBoundArrays()
        {
            if (bounds_planes.IsCreated)
                bounds_planes.Dispose();
        }

        public void CalcBoundPlanes(Bounds bounds, Quaternion rot, Vector3 position, Vector3 scale)
        {
            this.bounds_properties = new BoundsProperties() { bounds = bounds, position_offset = position, rotation = rot, scale = scale };
            DisposeBoundArrays();
            bounds_planes = new NativeArray<BoundsPlane>(bound_planes_count, Allocator.Persistent);

            var center = bounds.center + position;
            var forward = rot * Vector3.forward;
            var forward_scaled = forward * scale.z * bounds.extents.z;
            var right = rot * Vector3.right;
            var right_scaled = right * scale.x * bounds.extents.x;
            var up = rot * Vector3.up;
            var up_scaled = up * scale.y * bounds.extents.y;

            // add each wall of the bounds to an array, wall normals facing inward
            int wall_idx = 0;
            AddPlane(-forward, center - forward_scaled, wall_idx++);
            AddPlane(forward, center + forward_scaled, wall_idx++);
            AddPlane(-right, center - right_scaled, wall_idx++);
            AddPlane(right, center + right_scaled, wall_idx++);
            AddPlane(-up, center - up_scaled, wall_idx++);
            AddPlane(up, center + up_scaled, wall_idx++);

            // pointers need to be refreshed for the plane array
            pointers->CopyFrom(ref this);
        }

        private void AddPlane(float3 normal, float3 center, int idx)
        {
            bounds_planes[idx] = new BoundsPlane() { normal = normal, center = center };
        }

        // called when the application quits
        public void Dispose()
        {
            DisposeObjectArrays();
            DisposeBoundArrays();
        }

        // copies info from old arrays into the resized ones, spawns/disables spheres
        public unsafe void SetSize(int count)
        {
            if (count > 0)
            {
                var old_count = this.count;
                var temp_old_positions = new NativeArray<float3>(count, Allocator.Persistent);
                var temp_new_positions_arr = new NativeArray<float3>(count, Allocator.Persistent);
                var temp_velocities_arr = new NativeArray<float3>(count, Allocator.Persistent);
                var temp_old_colors_arr = new NativeArray<float3>(count, Allocator.Persistent);
                var temp_new_colors_arr = new NativeArray<float3>(count, Allocator.Persistent);
                var temp_color_dirty_arr = new NativeArray<bool>(count, Allocator.Persistent);
                var temp_in_collision_arr = new NativeArray<bool>(count, Allocator.Persistent);
                if (old_count > 0)
                {
                    // copy old data to new array
                    for (int i = 0; i < math.min(old_count, count); i++)
                    {
                        temp_old_positions[i] = old_positions[i];
                        temp_new_positions_arr[i] = new_positions[i];
                        temp_velocities_arr[i] = velocities[i];
                        temp_old_colors_arr[i] = original_colors[i];
                        temp_new_colors_arr[i] = final_colors[i];
                        temp_color_dirty_arr[i] = colors_dirty[i];
                        temp_in_collision_arr[i] = in_collision[i];
                    }
                    DisposeObjectArrays();
                }
                old_positions = temp_old_positions;
                new_positions = temp_new_positions_arr;
                velocities = temp_velocities_arr;
                original_colors = temp_old_colors_arr;
                final_colors = temp_new_colors_arr;
                colors_dirty = temp_color_dirty_arr;
                in_collision = temp_in_collision_arr;
                kdtree = new KDTree(count, Allocator.Persistent);
                this.count = count;
                pointers->CopyFrom(ref this);
                for (int i = old_count; i < count; i++)
                {
                    var inst = new JobObjectInstance(i, pointers->This);
                    inst.new_position = RandWithinBounds();
                    inst.velocity = RandomVelocity();
                    inst.original_color = RandomColor();
                    inst.old_position = inst.new_position;
                    inst.color_dirty = true;
                }

                // need to do this once when changing arrays, otherwise the sphere collision job throws errors for the tree not being initialized but being assigned to the job
                kdtree.BuildTree(new_positions).Complete();
            }
            else
            {
                DisposeObjectArrays();
                this.count = count;
                pointers->CopyFrom(ref this);
            }
        }

        #region RandomHelpers
        private float3 RandWithinBounds()
        {
            var bounds_scaled = bounds_properties.bounds;
            bounds_scaled.size *= bounds_properties.scale;
            var min = (float3)bounds_scaled.min + sphere_radius;
            var max = (float3)bounds_scaled.max - sphere_radius;
            var point_within_bounds_local = new float3(
                rand.NextFloat(min.x, max.x),
                rand.NextFloat(min.y, max.y),
                rand.NextFloat(min.z, max.z));
            var point_within_bounds_local_rotated = (float3)(bounds_properties.rotation * point_within_bounds_local);
            return point_within_bounds_local_rotated + bounds_properties.position_offset;
        }

        private float3 RandomVelocity()
        {
            float x_rot = rand.NextFloat(0, 360f);
            float y_rot = rand.NextFloat(0, 360f);
            var dir = Quaternion.Euler(x_rot, y_rot, 0) * Vector3.forward;
            return dir;
        }

        private float3 RandomColor()
        {
            return new float3(RandomZeroOne(), RandomZeroOne(), RandomZeroOne());
        }

        private float RandomZeroOne()
        {
            return rand.NextFloat(0, 1);
        }
        #endregion
    }

    // pointers/copies of most of the fields in Data
    public unsafe struct Pointers
    {
        public Pointers* This;

        public float sphere_radius;
        public float sphere_bounciness;
        // delta time
        public float dt;
        public int count;

        public float3* old_positions;
        public float3* new_positions;
        public float3* velocities;
        public float3* original_colors;
        public float3* final_colors;
        public bool* colors_dirty;
        public bool* in_collision;
        public BoundsPlane* bounds_planes;

        // get unsafe pointers from all the data arrays, copy non-array fields
        public void CopyFrom(ref Data data)
        {
            if (data.old_positions.IsCreated)
                old_positions = (float3*)data.old_positions.GetUnsafePtr();
            else
                old_positions = null;
            if (data.new_positions.IsCreated)
                new_positions = (float3*)data.new_positions.GetUnsafePtr();
            else
                new_positions = null;
            if (data.velocities.IsCreated)
                velocities = (float3*)data.velocities.GetUnsafePtr();
            else
                velocities = null;
            if (data.original_colors.IsCreated)
                original_colors = (float3*)data.original_colors.GetUnsafePtr();
            else
                original_colors = null;
            if (data.final_colors.IsCreated)
                final_colors = (float3*)data.final_colors.GetUnsafePtr();
            else
                final_colors = null;
            if (data.colors_dirty.IsCreated)
                colors_dirty = (bool*)data.colors_dirty.GetUnsafePtr();
            else
                colors_dirty = null;
            if (data.bounds_planes.IsCreated)
                bounds_planes = (BoundsPlane*)data.bounds_planes.GetUnsafePtr();
            else
                bounds_planes = null;
            if (data.in_collision.IsCreated)
                in_collision = (bool*)data.in_collision.GetUnsafePtr();
            else
                in_collision = null;

            count = data.count;
            sphere_radius = data.sphere_radius;
            sphere_bounciness = data.sphere_bounciness;
        }
    }

    // a representation of an instance of a sphere - easier way to access the arrays
    // just a collection of get/sets to the appropriate property array
    public unsafe struct JobObjectInstance
    {
        public Pointers* data;
        public int id;

        public JobObjectInstance(int id, Pointers* data)
        {
            this.id = id;
            this.data = data;
        }

        public float3 old_position
        {
            get { return data->old_positions[id]; }
            set { data->old_positions[id] = value; }
        }
        public float3 new_position
        {
            get { return data->new_positions[id]; }
            set { data->new_positions[id] = value; }
        }
        public float3 velocity
        {
            get { return data->velocities[id]; }
            set { data->velocities[id] = value; }
        }
        public float3 original_color
        {
            get { return data->original_colors[id]; }
            set { data->original_colors[id] = value; }
        }
        public float3 final_color
        {
            get { return data->final_colors[id]; }
            set { data->final_colors[id] = value; }
        }
        public bool color_dirty
        {
            get { return data->colors_dirty[id]; }
            set { data->colors_dirty[id] = value; }
        }
        public bool in_collision
        {
            get { return data->in_collision[id]; }
            set { data->in_collision[id] = value; }
        }
    }

    public unsafe struct BoundsPlane
    {
        public float3 center;
        public float3 normal;
    }

    public struct BoundsProperties
    {
        public Bounds bounds;
        public float3 position_offset;
        public Quaternion rotation;
        public float3 scale;
    }

    private InitJob init_job;
    private PositionVelocityUpdateJob position_velocity_update_job;
    private CalcCollisionBoundsJob collision_bounds_job;
    private CalcSphereCollisionsJob collision_sphere_job;
    private TransformUpdateJob transform_update_job;
    private JobHandle cur_job;
    #endregion

    private void Awake()
    {
        if (!Application.isPlaying)
            return;
        Init();
    }

    private void OnDestroy()
    {
        if (!Application.isPlaying)
            return;
        data.Dispose();
    }

    private unsafe void Init()
    {
        collision_between_spheres_last_frame = collisions_between_spheres;

        sphere_container = new GameObject("Sphere_Container").transform;
        sphere_visuals = new List<SphereVisuals>();
        mat_block = new MaterialPropertyBlock();

        onBoundsChanged += () =>
        {
            data.sphere_radius = sphere_radius;
            data.CalcBoundPlanes(bounds, transform.rotation, transform.position, transform.lossyScale);
            data.SetSize(0);
        };

        data = new Data();
        data.Init(bounds, transform.rotation, transform.position, transform.lossyScale, sphere_radius, 1);
        // create jobs. Assigning pointers here since they're always the same. Some other per-job properties are assigned in each update call
        init_job = new InitJob() { pointers = data.pointers };
        position_velocity_update_job = new PositionVelocityUpdateJob() { pointers = data.pointers };
        collision_bounds_job = new CalcCollisionBoundsJob() { pointers = data.pointers };
        collision_sphere_job = new CalcSphereCollisionsJob() { pointers = data.pointers, collision_color = new float3(1,0,0) }; 
        transform_update_job = new TransformUpdateJob() { pointers = data.pointers };
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        var old_matrix = Gizmos.matrix;
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawWireCube(bounds.center, bounds.size);
        Gizmos.matrix = old_matrix;
    }
#endif

    private unsafe void Update()
    {
        if (!Application.isPlaying)
            return;
        CheckBoundsForChanges();
        UpdateInput();
        UpdateObjectCount();
        UpdateRenderers();
        StartJobs();
    }

    private void CheckBoundsForChanges()
    {
        if (data.bounds_properties.bounds.Equals(bounds) && 
            data.bounds_properties.rotation == transform.rotation && 
            (Vector3)data.bounds_properties.position_offset == transform.position && 
            (Vector3)data.bounds_properties.scale == transform.lossyScale &&
            data.sphere_radius == sphere_radius)
            return;
        onBoundsChanged?.Invoke();
    }

    private void UpdateInput()
    {
        if (Input.GetMouseButtonDown(0))
        {
            var to_spawn = data.rand.NextInt(spheres_to_spawn_on_click_min, spheres_to_spawn_on_click_max);
            desired_obj_count += to_spawn;
        }
        if (Input.GetMouseButtonDown(1))
        {
            var to_spawn = data.rand.NextInt(spheres_to_spawn_on_click_min, spheres_to_spawn_on_click_max);
            desired_obj_count -= to_spawn;
        }
    }

    // count how many of the pooled spheres are active
    private int ActiveSphereVisuals()
    {
        int res = 0;
        for (int i = 0; i < sphere_visuals.Count; i++)
        {
            var sv = sphere_visuals[i];
            if (sv.active)
                res++;
        }
        return res;
    }

    // try to activate a pooled sphere. If there is no inactive one, spawn a new one, add it to the pool
    private SphereVisuals SpawnSphere()
    {
        for (int i = 0; i < sphere_visuals.Count; i++)
        {
            var sv = sphere_visuals[i];
            if (!sv.active)
            {
                sv.Enable(true);
                return sv;
            }
        }
        var go = Instantiate(obj_prefab, sphere_container);
        SphereVisuals sphere_data = new SphereVisuals();
        sphere_data.transform = go.transform;
        sphere_data.renderers = go.GetComponentsInChildren<Renderer>();
        sphere_data.active = true;
        sphere_visuals.Add(sphere_data);
        return sphere_data;
    }

    // update data/visual arrays whenever the number of objects changes
    private unsafe void UpdateObjectCount()
    {
        if (desired_obj_count < 0)
            desired_obj_count = 0;
        if (data.pointers->count != desired_obj_count)
        {
            // job data
            data.SetSize(desired_obj_count);
            // enable/disable spheres if increasing/lowering count
            var active_sphere_visuals = ActiveSphereVisuals();
            for (int i = active_sphere_visuals; i < desired_obj_count; i++)
            {
                SpawnSphere();
            }
            for (int i = active_sphere_visuals - 1; i >= desired_obj_count; i--)
            {
                var sphere_data = sphere_visuals[i];
                sphere_data.Enable(false);
            }

            // create transform access array
            if (desired_obj_count > 0)
            {
                Transform[] new_transform_arr = new Transform[desired_obj_count];
                var final_scale = Vector3.one * sphere_radius * 2;
                for (int i = 0; i < desired_obj_count; i++)
                {
                    new_transform_arr[i] = sphere_visuals[i].transform;
                    sphere_visuals[i].transform.localScale = final_scale;
                }
                data.taa = new TransformAccessArray(new_transform_arr);
            }
        }
    }

    private unsafe void UpdateRenderers()
    {
        // minor hack, if you disable the collision jobs, there's nobody to tell the spheres that they're supposed to go back to their original color
        bool collision_has_been_toggled = collision_between_spheres_last_frame != collisions_between_spheres;
        for(int i = 0; i < data.count; i++)
        {
            var instance = new JobObjectInstance(i, data.pointers);
            if (!instance.color_dirty && !collision_has_been_toggled)
                continue;
            var renderers = sphere_visuals[i].renderers;
            if (!collisions_between_spheres)
                mat_block.SetColor("_Color", new Color(instance.original_color.x, instance.original_color.y, instance.original_color.z, 1));
            else
                mat_block.SetColor("_Color", new Color(instance.final_color.x, instance.final_color.y, instance.final_color.z, 1));
            for(int j = 0; j < renderers.Length; j++)
            {
                var rend = renderers[j];
                rend.SetPropertyBlock(mat_block);
            }
        }
        collision_between_spheres_last_frame = collisions_between_spheres;
    }

    #region Jobs

    private unsafe void StartJobs()
    {
        if (data.pointers->count > 0)
        {
            data.pointers->dt = Time.deltaTime;
            cur_job = init_job.Schedule(data.pointers->count, 64, cur_job);
            cur_job = position_velocity_update_job.Schedule(data.pointers->count, 64, cur_job);
            cur_job = collision_bounds_job.Schedule(data.pointers->count, 64, cur_job);
            // option to enable/disable sphere collisions
            if (collisions_between_spheres)
            {
                collision_sphere_job.kdtree = data.kdtree;
                cur_job = data.kdtree.BuildTree(data.new_positions, cur_job);
                cur_job = collision_sphere_job.Schedule(data.pointers->count, 64, cur_job);
            }
            cur_job = transform_update_job.Schedule(data.taa, cur_job);
            cur_job.Complete();
        }
    }

    // reset any per-frame properties
    [BurstCompile]
    private unsafe struct InitJob : IJobParallelFor
    {
        [NativeDisableUnsafePtrRestriction]
        public Pointers* pointers;
        public void Execute(int index)
        {
            var instance = new JobObjectInstance(index, pointers);
            instance.old_position = instance.new_position;
            instance.in_collision = false;
            instance.color_dirty = false;
        }
    }

    // calculate new position by adding velocity to old position
    // could have been combined with other jobs, but in case we want to disable them or something, i've kept them separate
    [BurstCompile]
    private unsafe struct PositionVelocityUpdateJob : IJobParallelFor
    {
        [NativeDisableUnsafePtrRestriction]
        public Pointers* pointers;
        public void Execute(int index)
        {
            var instance = new JobObjectInstance(index, pointers);
            instance.new_position = instance.old_position + instance.velocity * pointers->dt;
        }
    }

    // figure out if/where the current velocity will pierce the bound planes, check if the distance to that point is below the sphere radius
    // if colliding, bounce across the plane
    [BurstCompile]
    private unsafe struct CalcCollisionBoundsJob : IJobParallelFor
    {
        [NativeDisableUnsafePtrRestriction]
        public Pointers* pointers;

        public void Execute(int index)
        {
            var instance = new JobObjectInstance(index, pointers);
            var ray_origin = instance.old_position;
            for (int i = 0; i < Data.bound_planes_count; i++)
            {
                var ray_dir = instance.velocity;
                var bound = pointers->bounds_planes[i];
                var plane_origin = bound.center;
                var plane_normal = bound.normal;
                float denominator = math.dot(instance.velocity, bound.normal);
                if (denominator > 0.00001f)
                {
                    //The distance to the plane
                    float t = math.dot(plane_origin - ray_origin, plane_normal) / denominator;

                    //Where the ray intersects with a plane
                    float3 p = ray_origin + ray_dir * t;

                    var dir_to_intersection = p - ray_origin;
                    var d = math.dot(dir_to_intersection, plane_normal);
                    var penetration = pointers->sphere_radius - d;

                    if (penetration > 0.0001f)
                    {
                        instance.new_position = ray_origin + penetration * plane_normal;
                        instance.velocity = Reflect(ray_dir, plane_normal, pointers->sphere_bounciness);
                    }
                }
            }
        }

        private float3 Reflect(float3 vec, float3 normal, float restitution)
        {
            float3 perpendicular = math.project(vec, normal);
            float3 parallel = vec - perpendicular;
            return parallel - restitution * perpendicular;
        }
    }

    // Check collisions between spheres. If colliding, mark them as collided, change colors
    // If already marked as collided, don't check collisions again
    [BurstCompile]
    private unsafe struct CalcSphereCollisionsJob : IJobParallelFor
    {
        public KDTree kdtree;
        [NativeDisableUnsafePtrRestriction]
        public Pointers* pointers;
        public float3 collision_color;

        public void Execute(int index)
        {
            var instance = new JobObjectInstance(index, pointers);
            float3 new_final_color;
            if (instance.in_collision)
            {
                new_final_color = collision_color;
            }
            else
            {
                float r2 = 2 * pointers->sphere_radius;
                float rsq = r2 * r2;
                // This allocation is one thing i don't like. A single (max number of neighbors) array of neighbours supplied outside of the job can't work, since it would have to be accessed multithreaded
                // The supplied KDTree requires a nativearray as an input. 
                // Ideally i would have 1 big array in the data/pointers (sphere count * max number of neighbours) and i'd just be filling parts of it offsetted
                // As it stands, with burst turned off, in the profiler this is roughly a 21% slowdown compared to it potentially being 1 array
                // But with burst turned on, the collision job was 0.58ms with 6000 spheres, so i figured it was fast enough as is. If needed, it can be sped up as described above
                NativeArray<KDTree.Neighbour> neighbors = new NativeArray<KDTree.Neighbour>(Data.max_collisions_between_spheres, Allocator.Temp);
                // check current object position for collisions, all spheres have the same radius so we just multiply it by 2 
                int num_collisions = kdtree.GetEntriesInRange(instance.new_position, pointers->sphere_radius * 2, ref neighbors);
                for (int i = 0; i < num_collisions; i++)
                {
                    var neighbour_id = neighbors[i].index;
                    // ignore yourself, you're always colliding with yourself
                    if (index == neighbour_id)
                        continue;
                    var neighbour = new JobObjectInstance(neighbour_id, pointers);
                    neighbour.in_collision = true;
                }
                if (num_collisions > 1)
                {
                    new_final_color = collision_color;
                    instance.in_collision = true;
                }
                else
                    new_final_color = instance.original_color;
                neighbors.Dispose();
            }
            if (!new_final_color.Equals(instance.final_color))
            {
                instance.final_color = new_final_color;
                instance.color_dirty = true;
            }
        }
    }

    // apply final position to transforms
    [BurstCompile]
    private unsafe struct TransformUpdateJob : IJobParallelForTransform
    {
        [NativeDisableUnsafePtrRestriction]
        public Pointers* pointers;
        public void Execute(int index, TransformAccess transform)
        {
            var instance = new JobObjectInstance(index, pointers);
            transform.position = instance.new_position;
        }
    }
    #endregion
}
