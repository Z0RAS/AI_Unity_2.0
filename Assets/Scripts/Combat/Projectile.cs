using UnityEngine;

public class Projectile : MonoBehaviour
{
    Transform target;
    float damage;
    float speed;

    public void Init(Transform target, float damage, float speed)
    {
        this.target = target;
        this.damage = damage;
        this.speed = speed;
    }

    private void Update()
    {
        if (target == null)
        {
            Destroy(gameObject);
            return;
        }

        Vector3 dir = (target.position - transform.position).normalized;
        transform.position += dir * speed * Time.deltaTime;

        if (Vector2.Distance(transform.position, target.position) < 0.2f)
        {
            // Try unit combat
            UnitCombat combat = target.GetComponent<UnitCombat>();
            if (combat != null)
            {
                combat.TakeDamage(damage);
                Destroy(gameObject);
                return;
            }

            // Try Building
            Building b = target.GetComponent<Building>();
            if (b != null)
            {
                b.TakeDamage(damage);
                Destroy(gameObject);
                return;
            }

            // Try BuildingConstruction
            BuildingConstruction bc = target.GetComponent<BuildingConstruction>();
            if (bc != null)
            {
                bc.TakeDamage(damage);
                Destroy(gameObject);
                return;
            }

            Destroy(gameObject);
        }
    }
}